using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PromoEngine.Domain.Catalog;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// Development provisioner. Creates a database per tenant on a local PostgreSQL
/// server, so the multi-tenant flow can be exercised end to end without AWS
/// credentials. The AWS provisioner does the same thing against RDS.
/// </summary>
public sealed class LocalPostgresTenantProvisioner(
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<LocalProvisioningOptions> localOptions,
    TenantSchemaInitializer schemaInitializer,
    ILogger<LocalPostgresTenantProvisioner> logger) : ITenantProvisioner
{
    private readonly TenancyOptions _tenancy = tenancyOptions.Value;
    private readonly LocalProvisioningOptions _local = localOptions.Value;

    public string Name => "Local";

    public async Task<ProvisioningResult> ProvisionAsync(Tenant tenant, CancellationToken ct = default)
    {
        var databaseName = TenantDatabaseNaming.BuildDatabaseName(_tenancy.DatabaseNamePrefix, tenant.Slug);

        try
        {
            await using (var connection = new NpgsqlConnection(_local.ServerConnectionString))
            {
                await connection.OpenAsync(ct);
                await PostgresDatabaseCommands.CreateDatabaseAsync(connection, databaseName, ct);
            }

            var builder = new NpgsqlConnectionStringBuilder(_local.ServerConnectionString)
            {
                Database = databaseName
            };
            var connectionString = builder.ConnectionString;

            await schemaInitializer.InitializeAsync(connectionString, _tenancy.SeedSampleData, ct);

            logger.LogInformation("Provisioned local tenant database {Database} for {Email}", databaseName, tenant.Email);
            return ProvisioningResult.Ok(databaseName, connectionString);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision local database {Database}", databaseName);
            return ProvisioningResult.Fail(ex.Message);
        }
    }

    public async Task DeprovisionAsync(Tenant tenant, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.DatabaseName))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_local.ServerConnectionString);
        await connection.OpenAsync(ct);

        await PostgresDatabaseCommands.DropDatabaseAsync(connection, tenant.DatabaseName, ct);

        logger.LogWarning("Dropped local tenant database {Database}", tenant.DatabaseName);
    }
}

/// <summary>
/// Creating and dropping a database, written once because the local and RDS
/// provisioners need exactly the same statements and PostgreSQL makes both of them
/// more awkward than SQL Server did.
/// </summary>
internal static class PostgresDatabaseCommands
{
    /// <summary>
    /// PostgreSQL has no <c>CREATE DATABASE IF NOT EXISTS</c>, so existence is checked
    /// first against pg_database. CREATE DATABASE also cannot run inside a
    /// transaction, which is why these commands are issued on a bare connection
    /// rather than through EF.
    /// </summary>
    public static async Task CreateDatabaseAsync(
        NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", databaseName);
            if (await exists.ExecuteScalarAsync(ct) is not null) return;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE {Quote(databaseName)}", connection)
        {
            CommandTimeout = 180
        };

        try
        {
            await create.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // Two provisioning attempts raced. The database exists, which is all the
            // caller wanted, so this is success rather than a failure to report.
        }
    }

    /// <summary>
    /// PostgreSQL refuses to drop a database that still has sessions attached, so the
    /// backends are terminated first. This is the equivalent of the SQL Server
    /// <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> that preceded the old DROP.
    /// </summary>
    public static async Task DropDatabaseAsync(
        NpgsqlConnection connection, string databaseName, CancellationToken ct)
    {
        await using (var terminate = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @name AND pid <> pg_backend_pid()
            """, connection))
        {
            terminate.Parameters.AddWithValue("name", databaseName);
            await terminate.ExecuteNonQueryAsync(ct);
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Quote(databaseName)}", connection)
        {
            CommandTimeout = 180
        };

        await drop.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The database name is built from a sanitised slug and cannot contain a quote,
    /// but it is escaped anyway: an identifier cannot be parameterised, so this is the
    /// only thing standing between the name and the statement.
    /// </summary>
    internal static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}

/// <summary>Turns an email address into a database-safe tenant identifier.</summary>
public static class TenantDatabaseNaming
{
    /// <summary>
    /// PostgreSQL truncates any identifier past 63 bytes rather than rejecting it, so
    /// two long tenant names could silently collapse onto the same database. Names are
    /// kept inside the limit here instead. SQL Server allowed 128, so this only ever
    /// affects names that were already close to unworkable.
    /// </summary>
    private const int MaxIdentifierBytes = 63;

    /// <summary>
    /// jane.doe@contoso.com becomes jane-doe-contoso-com. Only letters, digits and
    /// dashes survive, which keeps the value safe to embed in an identifier and
    /// usable in a URL.
    /// </summary>
    public static string BuildSlug(string email)
    {
        var chars = email
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');

        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }

    public static string BuildDatabaseName(string prefix, string slug)
    {
        var name = $"{prefix}{slug.Replace('-', '_')}";
        if (name.Length <= MaxIdentifierBytes) return name;

        // Truncating alone could map two different tenants onto one name, so the part
        // that is cut off is replaced by a hash of the whole name. The result is
        // deterministic: the same slug always resolves to the same database.
        var suffix = "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();

        return name[..(MaxIdentifierBytes - suffix.Length)] + suffix;
    }
}
