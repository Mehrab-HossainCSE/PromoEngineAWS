using System.Text.RegularExpressions;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using PromoEngine.Infrastructure.Tenancy;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// Brings an existing tenant database up to date with the current EF model.
///
/// The tenant schema is created with EnsureCreated rather than migrations, and
/// EnsureCreated only ever creates a database that is missing - it never alters one
/// that already exists. So the first time a property is added to an entity, every
/// database created before that change starts failing with "Invalid column name".
/// This closes that gap by adding what is missing, which keeps the no-migrations
/// workflow usable as the model evolves.
///
/// It is deliberately additive: it creates missing tables and adds missing columns,
/// and never drops or retypes anything it was not explicitly told to. The one
/// exception is <see cref="RetiredTables"/>, a hand-written list of tables the
/// application has genuinely removed - nothing infers a drop from a table simply
/// being absent from the model, because that would delete anything a DBA had added
/// alongside ours.
/// </summary>
public sealed partial class TenantSchemaReconciler(
    ITenantDbContextFactory contextFactory,
    ILogger<TenantSchemaReconciler> logger)
{
    /// <summary>
    /// Tables this application used to create and has since removed. Listing one here
    /// drops it from every tenant database, so a name only belongs here once the code
    /// that read it is gone and the data in it is genuinely dead.
    ///
    /// PromotionProducts held the barcode sheet when it was uploaded against a
    /// promotion. That sheet now belongs to an offer and is stored in OfferProducts
    /// with a different shape - no discount columns, an offer key instead of a
    /// promotion key - so there is nothing in the old table to carry across.
    /// </summary>
    private static readonly string[] RetiredTables = ["PromotionProducts"];

    public sealed record ReconcileResult(
        int TablesCreated, int ColumnsAdded, int TablesDropped, IReadOnlyList<string> Notes)
    {
        public bool ChangedAnything => TablesCreated > 0 || ColumnsAdded > 0 || TablesDropped > 0;
    }

    public async Task<ReconcileResult> ReconcileAsync(string connectionString, CancellationToken ct = default)
    {
        await using var db = contextFactory.CreateForConnectionString(connectionString);

        var notes = new List<string>();
        var tablesCreated = 0;
        var columnsAdded = 0;

        // Tables and columns below can carry a COLLATE clause, and PostgreSQL rejects
        // one naming a collation that does not exist. A database created before the
        // collation was introduced has none, so it is put in place first.
        await EnsureCollationsAsync(db, notes, ct);

        var existing = await ReadSchemaAsync(db, ct);
        var tablesDropped = await DropRetiredTablesAsync(db, existing, notes, ct);

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrWhiteSpace(table)) continue;

            var schema = entity.GetSchema() ?? DefaultSchema;
            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());

            if (!existing.TryGetValue(table, out var columns))
            {
                // A whole table is missing, which happens when a new DbSet is added.
                if (await CreateTableAsync(db, table, ct))
                {
                    tablesCreated++;
                    notes.Add($"created table {table}");
                }
                else
                {
                    notes.Add($"could not create missing table {table}");
                }

                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (string.IsNullOrWhiteSpace(column) || columns.Contains(column)) continue;

                var columnType = property.GetColumnType();
                var nullable = property.IsColumnNullable(storeObject);

                // A non-nullable column needs a default, otherwise the ALTER fails on a
                // table that already has rows.
                var defaultClause = nullable ? string.Empty : $" DEFAULT {DefaultFor(columnType)}";
                var nullClause = nullable ? "NULL" : "NOT NULL";

                // GetColumnType does not include the collation, and a column added
                // without it would silently compare case-sensitively while the same
                // column on a freshly created database did not.
                var collation = property.GetCollation();
                var collateClause = string.IsNullOrWhiteSpace(collation) ? string.Empty : $" COLLATE {collation}";

                var sql =
                    $"ALTER TABLE {Quote(schema)}.{Quote(table)} "
                    + $"ADD COLUMN {Quote(column)} {columnType}{collateClause} {nullClause}{defaultClause};";

                try
                {
                    await db.Database.ExecuteSqlRawAsync(sql, ct);
                    columnsAdded++;
                    notes.Add($"added {table}.{column} ({columnType}{(nullable ? "" : ", not null with default")})");
                }
                catch (NpgsqlException ex)
                {
                    logger.LogError(ex, "Could not add column {Table}.{Column}", table, column);
                    notes.Add($"FAILED to add {table}.{column}: {ex.Message}");
                }
            }
        }

        if (tablesCreated > 0 || columnsAdded > 0 || tablesDropped > 0)
        {
            logger.LogWarning(
                "Reconciled tenant schema: {Tables} table(s) created, {Columns} column(s) added, "
                + "{Dropped} retired table(s) dropped. {Notes}",
                tablesCreated, columnsAdded, tablesDropped, string.Join("; ", notes));
        }

        return new ReconcileResult(tablesCreated, columnsAdded, tablesDropped, notes);
    }

    /// <summary>
    /// Creates any collation the model declares that the database does not have yet.
    ///
    /// PostgreSQL has no CREATE COLLATION IF NOT EXISTS, so the statements are lifted
    /// out of the model's own create script - the same source EnsureCreated uses, so
    /// the two can never define the collation differently - and a duplicate is treated
    /// as success rather than an error.
    /// </summary>
    private async Task EnsureCollationsAsync(TenantDbContext db, List<string> notes, CancellationToken ct)
    {
        foreach (var statement in CollationStatements(db))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(statement, ct);
                notes.Add("created missing collation");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject)
            {
                // Already there, which is the state we wanted.
            }
            catch (NpgsqlException ex)
            {
                // Worth reporting but not worth stopping for: only the case-insensitive
                // columns depend on it, and the rest of the reconcile is still useful.
                logger.LogError(ex, "Could not create collation on tenant database");
                notes.Add($"FAILED to create collation: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> CollationStatements(TenantDbContext db) =>
        SplitScript(db.GetService<IRelationalDatabaseCreator>().GenerateCreateScript())
            .Where(sql => sql.StartsWith("CREATE COLLATION", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits the model's create script into statements. Npgsql terminates each with a
    /// semicolon and separates them with a blank line; PostgreSQL has no GO batches.
    /// </summary>
    private static List<string> SplitScript(string script) => script
        .Split([";\r\n\r\n", ";\n\n"], StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim().TrimEnd(';').Trim())
        .Where(s => s.Length > 0)
        .ToList();

    /// <summary>
    /// Drops the tables named in <see cref="RetiredTables"/>. The row count is logged
    /// before the drop, so a database that still held data leaves a record of how much
    /// went, rather than the table simply being gone one day.
    /// </summary>
    private async Task<int> DropRetiredTablesAsync(
        TenantDbContext db, Dictionary<string, HashSet<string>> existing, List<string> notes, CancellationToken ct)
    {
        var dropped = 0;

        foreach (var table in RetiredTables.Where(existing.ContainsKey))
        {
            // The name comes from the RetiredTables constant above, never from input,
            // and is quoted regardless. Built outside the call so the raw-SQL analyser
            // is not left guessing about an interpolated literal.
            var qualified = $"{Quote(DefaultSchema)}.{Quote(table)}";
            var countSql = $"SELECT COUNT(*) AS {Quote("Value")} FROM {qualified}";
            var dropSql = $"DROP TABLE {qualified};";

            try
            {
                var rows = await db.Database.SqlQueryRaw<long>(countSql).SingleAsync(ct);

                await db.Database.ExecuteSqlRawAsync(dropSql, ct);

                dropped++;
                notes.Add($"dropped retired table {table} ({rows} row(s))");
                logger.LogWarning("Dropped retired tenant table {Table} holding {Rows} row(s)", table, rows);
            }
            catch (NpgsqlException ex)
            {
                logger.LogError(ex, "Could not drop retired table {Table}", table);
                notes.Add($"FAILED to drop retired table {table}: {ex.Message}");
            }
        }

        return dropped;
    }

    /// <summary>Table name to its set of column names, as the database currently has them.</summary>
    private static async Task<Dictionary<string, HashSet<string>>> ReadSchemaAsync(
        TenantDbContext db, CancellationToken ct)
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var connection = db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'public';";

        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);

            if (!schema.TryGetValue(table, out var columns))
            {
                columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                schema[table] = columns;
            }

            columns.Add(column);
        }

        return schema;
    }

    /// <summary>
    /// Pulls the CREATE TABLE statement for one table out of the model's full create
    /// script, so a newly added entity gets exactly the table EF would have made, then
    /// applies the indexes that table declares.
    /// </summary>
    private async Task<bool> CreateTableAsync(TenantDbContext db, string table, CancellationToken ct)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        var script = creator.GenerateCreateScript();

        var statements = SplitScript(script);

        // EF writes the default schema as a bare "Table", not "public"."Table", so the
        // schema prefix has to be optional or nothing matches. The name is required to
        // be quoted, otherwise "Offer" would also match inside "OfferItems".
        var name = Regex.Escape(table);
        var options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var q = Regex.Escape("\"");

        var tablePattern = new Regex(
            $@"CREATE\s+TABLE\s+({q}public{q}\.)?{q}{name}{q}", options);

        var indexPattern = new Regex(
            $@"CREATE\s+(UNIQUE\s+)?INDEX\s+{q}[^{q}]+{q}\s+ON\s+({q}public{q}\.)?{q}{name}{q}", options);

        var create = statements.FirstOrDefault(sql => tablePattern.IsMatch(sql));

        if (create is null)
        {
            logger.LogError("No CREATE TABLE statement for {Table} was found in the model create script", table);
            return false;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(create, ct);
        }
        catch (NpgsqlException ex)
        {
            // Foreign keys to tables that do not exist yet are expected on a first
            // pass; the caller reruns until the set stops shrinking.
            logger.LogWarning(ex, "Could not create table {Table} yet", table);
            return false;
        }

        // The table is usable without its indexes, so one that fails is logged rather
        // than reported as the table itself having failed.
        foreach (var index in statements.Where(sql => indexPattern.IsMatch(sql)))
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(index, ct);
            }
            catch (NpgsqlException ex)
            {
                logger.LogWarning(ex, "Created {Table} but could not add one of its indexes", table);
            }
        }

        return true;
    }

    /// <summary>
    /// A literal safe to use as a DEFAULT for a newly added non-nullable column, keyed
    /// on the PostgreSQL store type EF reports. The bare type is taken before any
    /// length or precision, and before the "with time zone" suffix, so numeric(18,4)
    /// and "timestamp with time zone" both resolve.
    /// </summary>
    private static string DefaultFor(string columnType)
    {
        var bare = columnType.Split('(')[0].Trim().ToLowerInvariant();

        return bare switch
        {
            "boolean" or "bool" => "false",
            "smallint" or "integer" or "int" or "int2" or "int4" or "int8" or "bigint" => "0",
            "numeric" or "decimal" or "money" or "real" or "double precision" or "float4" or "float8" => "0",
            "uuid" => "'00000000-0000-0000-0000-000000000000'",
            "date" => "DATE '1900-01-01'",
            "timestamp" or "timestamp without time zone" => "TIMESTAMP '1900-01-01 00:00:00'",
            "timestamptz" or "timestamp with time zone" => "TIMESTAMPTZ '1900-01-01 00:00:00+00'",
            "time" or "time without time zone" => "TIME '00:00:00'",
            "interval" => "INTERVAL '0'",
            "bytea" => "'\\x'::bytea",
            "jsonb" => "'{}'::jsonb",
            "json" => "'{}'::json",
            _ => "''"
        };
    }

    /// <summary>PostgreSQL's default schema. SQL Server called this dbo.</summary>
    private const string DefaultSchema = "public";

    /// <summary>
    /// Wraps an identifier in double quotes, which is what PostgreSQL uses and what
    /// EF emits. Quoting also preserves the PascalCase table and column names, which
    /// PostgreSQL would otherwise fold to lower case.
    /// </summary>
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
