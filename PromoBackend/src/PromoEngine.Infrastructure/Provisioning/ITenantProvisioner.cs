using PromoEngine.Domain.Catalog;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>Result of creating the physical database for a tenant.</summary>
public sealed record ProvisioningResult
{
    public required bool Success { get; init; }
    public string? DatabaseName { get; init; }
    public string? ConnectionString { get; init; }
    public string? InstanceIdentifier { get; init; }
    public string? Error { get; init; }

    public static ProvisioningResult Ok(string databaseName, string connectionString, string? instanceIdentifier = null) =>
        new()
        {
            Success = true,
            DatabaseName = databaseName,
            ConnectionString = connectionString,
            InstanceIdentifier = instanceIdentifier
        };

    public static ProvisioningResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Creates and removes the isolated database that backs a tenant. Implementations
/// exist for a local SQL Server (development) and for Amazon RDS (production);
/// which one runs is chosen by the Tenancy:Provisioner setting.
/// </summary>
public interface ITenantProvisioner
{
    /// <summary>Name reported back to the catalog, for example "Local" or "Aws".</summary>
    string Name { get; }

    /// <summary>Creates the database and applies the promotion schema to it.</summary>
    Task<ProvisioningResult> ProvisionAsync(Tenant tenant, CancellationToken ct = default);

    /// <summary>Drops the tenant database. Used when a sign up is rolled back.</summary>
    Task DeprovisionAsync(Tenant tenant, CancellationToken ct = default);
}

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>"Local" or "Aws".</summary>
    public string Provisioner { get; set; } = "Local";

    /// <summary>Prefix applied to every tenant database name.</summary>
    public string DatabaseNamePrefix { get; set; } = "PromoEngine_Tenant_";

    /// <summary>Seed each freshly created tenant database with demo campaigns and stores.</summary>
    public bool SeedSampleData { get; set; } = true;

    /// <summary>Synchronize tenant database schemas to match the EF Core model on startup.</summary>
    public bool AutoSyncSchemaOnStartup { get; set; } = true;
}

public sealed class LocalProvisioningOptions
{
    public const string SectionName = "Tenancy:Local";

    /// <summary>
    /// Connection string to the local PostgreSQL server that hosts tenant databases.
    /// It points at the maintenance database, because CREATE DATABASE has to be
    /// issued from a connection to some other database.
    /// </summary>
    public string ServerConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";
}

public sealed class AwsProvisioningOptions
{
    public const string SectionName = "Tenancy:Aws";

    /// <summary>
    /// DatabaseOnSharedInstance creates one database per tenant on an existing RDS
    /// instance (fast, the normal choice). DedicatedInstance calls CreateDBInstance
    /// so the tenant gets its own RDS instance (slow, minutes, premium plans only).
    /// </summary>
    public string Mode { get; set; } = "DatabaseOnSharedInstance";

    public string Region { get; set; } = "us-east-1";

    /// <summary>Identifier of the shared RDS instance used by DatabaseOnSharedInstance.</summary>
    public string? SharedInstanceIdentifier { get; set; }

    /// <summary>Endpoint of the shared RDS instance, for example promo-shared.abc123.us-east-1.rds.amazonaws.com.</summary>
    public string? SharedInstanceEndpoint { get; set; }

    public string MasterUsername { get; set; } = "promoadmin";

    /// <summary>Port the RDS instance listens on. 5432 is the PostgreSQL default.</summary>
    public int Port { get; set; } = 5432;

    /// <summary>
    /// Master password. In production read this from AWS Secrets Manager rather than
    /// configuration; it is here so the provisioner can be exercised end to end.
    /// </summary>
    public string? MasterPassword { get; set; }

    // Settings used only by DedicatedInstance mode.
    public string InstanceClass { get; set; } = "db.t3.small";
    public string Engine { get; set; } = "postgres";
    public string EngineVersion { get; set; } = "16.4";
    public int AllocatedStorageGb { get; set; } = 20;
    public string? DbSubnetGroupName { get; set; }
    public List<string> VpcSecurityGroupIds { get; set; } = [];
    public bool PubliclyAccessible { get; set; }
    public int WaitTimeoutMinutes { get; set; } = 30;
}
