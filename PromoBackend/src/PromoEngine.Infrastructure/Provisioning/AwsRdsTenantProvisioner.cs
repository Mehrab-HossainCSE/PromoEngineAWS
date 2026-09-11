using Amazon;
using Amazon.RDS;
using Amazon.RDS.Model;
using Npgsql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromoEngine.Domain.Catalog;

namespace PromoEngine.Infrastructure.Provisioning;

/// <summary>
/// Creates the tenant database in AWS.
///
/// DatabaseOnSharedInstance (default) creates one database per tenant on an existing
/// RDS for PostgreSQL instance. This is the mode to use for most customers: it takes
/// seconds and still gives each tenant a physically separate database with its own
/// credentials scope.
///
/// DedicatedInstance calls CreateDBInstance so the tenant gets an RDS instance of its
/// own. Provisioning takes several minutes, so it runs on the background queue and the
/// UI polls the provisioning status.
/// </summary>
public sealed class AwsRdsTenantProvisioner(
    IOptions<TenancyOptions> tenancyOptions,
    IOptions<AwsProvisioningOptions> awsOptions,
    TenantSchemaInitializer schemaInitializer,
    ILogger<AwsRdsTenantProvisioner> logger) : ITenantProvisioner, IDisposable
{
    /// <summary>
    /// PostgreSQL's always-present administrative database, connected to in order to
    /// create or drop another one. SQL Server called this master.
    /// </summary>
    private const string MaintenanceDatabase = "postgres";

    private readonly TenancyOptions _tenancy = tenancyOptions.Value;
    private readonly AwsProvisioningOptions _aws = awsOptions.Value;
    private AmazonRDSClient? _client;

    public string Name => "Aws";

    private AmazonRDSClient Client =>
        _client ??= new AmazonRDSClient(RegionEndpoint.GetBySystemName(_aws.Region));

    public async Task<ProvisioningResult> ProvisionAsync(Tenant tenant, CancellationToken ct = default)
    {
        var databaseName = TenantDatabaseNaming.BuildDatabaseName(_tenancy.DatabaseNamePrefix, tenant.Slug);

        try
        {
            return _aws.Mode.Equals("DedicatedInstance", StringComparison.OrdinalIgnoreCase)
                ? await ProvisionDedicatedInstanceAsync(tenant, databaseName, ct)
                : await ProvisionOnSharedInstanceAsync(tenant, databaseName, ct);
        }
        catch (AmazonRDSException ex)
        {
            logger.LogError(ex, "RDS rejected provisioning for tenant {Email}", tenant.Email);
            return ProvisioningResult.Fail($"AWS RDS error: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision AWS database for tenant {Email}", tenant.Email);
            return ProvisioningResult.Fail(ex.Message);
        }
    }

    private async Task<ProvisioningResult> ProvisionOnSharedInstanceAsync(
        Tenant tenant, string databaseName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_aws.SharedInstanceEndpoint))
        {
            return ProvisioningResult.Fail(
                "Tenancy:Aws:SharedInstanceEndpoint is not configured. Set it to the endpoint of the shared RDS instance, or switch Mode to DedicatedInstance.");
        }

        if (string.IsNullOrWhiteSpace(_aws.MasterPassword))
        {
            return ProvisioningResult.Fail("Tenancy:Aws:MasterPassword is not configured.");
        }

        // Confirm the shared instance is actually available before trying to connect,
        // so a misconfiguration surfaces as a clear message rather than a socket timeout.
        if (!string.IsNullOrWhiteSpace(_aws.SharedInstanceIdentifier))
        {
            var described = await Client.DescribeDBInstancesAsync(
                new DescribeDBInstancesRequest { DBInstanceIdentifier = _aws.SharedInstanceIdentifier }, ct);

            var status = described.DBInstances.FirstOrDefault()?.DBInstanceStatus;
            if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
            {
                return ProvisioningResult.Fail($"Shared RDS instance is not available (status: {status ?? "unknown"}).");
            }
        }

        var maintenanceConnectionString = BuildConnectionString(_aws.SharedInstanceEndpoint!, MaintenanceDatabase);
        await CreateDatabaseAsync(maintenanceConnectionString, databaseName, ct);

        var tenantConnectionString = BuildConnectionString(_aws.SharedInstanceEndpoint!, databaseName);
        await schemaInitializer.InitializeAsync(tenantConnectionString, _tenancy.SeedSampleData, ct);

        logger.LogInformation(
            "Provisioned database {Database} on shared RDS instance {Instance} for {Email}",
            databaseName, _aws.SharedInstanceIdentifier, tenant.Email);

        return ProvisioningResult.Ok(databaseName, tenantConnectionString, _aws.SharedInstanceIdentifier);
    }

    private async Task<ProvisioningResult> ProvisionDedicatedInstanceAsync(
        Tenant tenant, string databaseName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_aws.MasterPassword))
        {
            return ProvisioningResult.Fail("Tenancy:Aws:MasterPassword is not configured.");
        }

        // RDS identifiers allow letters, digits and hyphens only, and must start with a letter.
        var instanceIdentifier = $"promo-{tenant.Slug}".Replace('_', '-');
        if (instanceIdentifier.Length > 63)
        {
            instanceIdentifier = instanceIdentifier[..63].TrimEnd('-');
        }

        var request = new CreateDBInstanceRequest
        {
            DBInstanceIdentifier = instanceIdentifier,
            DBInstanceClass = _aws.InstanceClass,
            Engine = _aws.Engine,
            EngineVersion = _aws.EngineVersion,
            AllocatedStorage = _aws.AllocatedStorageGb,
            MasterUsername = _aws.MasterUsername,
            MasterUserPassword = _aws.MasterPassword,
            PubliclyAccessible = _aws.PubliclyAccessible,
            BackupRetentionPeriod = 7,
            StorageEncrypted = true,
            Tags =
            [
                new Tag { Key = "Application", Value = "PromoEngine" },
                new Tag { Key = "TenantId", Value = tenant.Id.ToString() },
                new Tag { Key = "TenantEmail", Value = tenant.Email }
            ]
        };

        if (!string.IsNullOrWhiteSpace(_aws.DbSubnetGroupName))
        {
            request.DBSubnetGroupName = _aws.DbSubnetGroupName;
        }

        if (_aws.VpcSecurityGroupIds.Count > 0)
        {
            request.VpcSecurityGroupIds = _aws.VpcSecurityGroupIds;
        }

        await Client.CreateDBInstanceAsync(request, ct);
        logger.LogInformation("Requested RDS instance {Instance} for tenant {Email}", instanceIdentifier, tenant.Email);

        var endpoint = await WaitForInstanceAsync(instanceIdentifier, ct);
        if (endpoint is null)
        {
            return ProvisioningResult.Fail(
                $"RDS instance {instanceIdentifier} did not become available within {_aws.WaitTimeoutMinutes} minutes.");
        }

        var maintenanceConnectionString = BuildConnectionString(endpoint, MaintenanceDatabase);
        await CreateDatabaseAsync(maintenanceConnectionString, databaseName, ct);

        var tenantConnectionString = BuildConnectionString(endpoint, databaseName);
        await schemaInitializer.InitializeAsync(tenantConnectionString, _tenancy.SeedSampleData, ct);

        return ProvisioningResult.Ok(databaseName, tenantConnectionString, instanceIdentifier);
    }

    private async Task<string?> WaitForInstanceAsync(string instanceIdentifier, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(_aws.WaitTimeoutMinutes);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            var described = await Client.DescribeDBInstancesAsync(
                new DescribeDBInstancesRequest { DBInstanceIdentifier = instanceIdentifier }, ct);

            var instance = described.DBInstances.FirstOrDefault();
            if (instance is null)
            {
                continue;
            }

            logger.LogInformation("RDS instance {Instance} status {Status}", instanceIdentifier, instance.DBInstanceStatus);

            if (string.Equals(instance.DBInstanceStatus, "available", StringComparison.OrdinalIgnoreCase)
                && instance.Endpoint is not null)
            {
                return instance.Endpoint.Address;
            }

            if (string.Equals(instance.DBInstanceStatus, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return null;
    }

    private static async Task CreateDatabaseAsync(
        string maintenanceConnectionString, string databaseName, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(ct);

        await PostgresDatabaseCommands.CreateDatabaseAsync(connection, databaseName, ct);
    }

    private string BuildConnectionString(string endpoint, string database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = endpoint,
            Port = _aws.Port,
            Database = database,
            Username = _aws.MasterUsername,
            Password = _aws.MasterPassword,
            // RDS presents a certificate signed by the Amazon RDS root CA. Require is
            // encryption without chain verification, matching what the SQL Server
            // connection did with Encrypt plus TrustServerCertificate.
            SslMode = SslMode.Require,
            Timeout = 60
        }.ConnectionString;

    public async Task DeprovisionAsync(Tenant tenant, CancellationToken ct = default)
    {
        if (_aws.Mode.Equals("DedicatedInstance", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(tenant.RdsInstanceIdentifier))
        {
            await Client.DeleteDBInstanceAsync(new DeleteDBInstanceRequest
            {
                DBInstanceIdentifier = tenant.RdsInstanceIdentifier,
                SkipFinalSnapshot = false,
                FinalDBSnapshotIdentifier = $"{tenant.RdsInstanceIdentifier}-final-{DateTime.UtcNow:yyyyMMddHHmmss}"
            }, ct);

            logger.LogWarning("Requested deletion of RDS instance {Instance}", tenant.RdsInstanceIdentifier);
            return;
        }

        if (string.IsNullOrWhiteSpace(tenant.DatabaseName) || string.IsNullOrWhiteSpace(_aws.SharedInstanceEndpoint))
        {
            return;
        }

        await using var connection = new NpgsqlConnection(
            BuildConnectionString(_aws.SharedInstanceEndpoint!, MaintenanceDatabase));
        await connection.OpenAsync(ct);

        await PostgresDatabaseCommands.DropDatabaseAsync(connection, tenant.DatabaseName, ct);
    }

    public void Dispose() => _client?.Dispose();
}
