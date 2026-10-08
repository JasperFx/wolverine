namespace IntegrationTests;

public class Servers
{
    /// <summary>
    /// The connection string named <paramref name="name"/> in the .NET Aspire convention,
    /// <c>ConnectionStrings__{name}</c>; else the older <paramref name="variable"/>; else
    /// <paramref name="fallback"/>. Unset and blank are the same.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallbacks are the docker-compose values this repository has always used, so a developer
    /// or CI job that sets nothing behaves exactly as it did before.
    /// </para>
    /// <para>
    /// The override exists so that several test processes can run <em>concurrently</em>, each
    /// pointed at its own database. Schema names are hard-coded throughout the suite ("main",
    /// "wolverine", "registry", and the default), so two processes sharing one database collide on
    /// the same tables however the tests are partitioned — isolation has to be at the database, not
    /// the connection string alone.
    /// </para>
    /// <para>
    /// <c>ConnectionStrings__{name}</c> is how the Bobcat supervisor hands each worker its
    /// infrastructure (JasperFx/bobcat#414), and how an Aspire AppHost's <c>WithReference(...)</c>
    /// does. Either way the value is a complete connection string, never a fragment. The
    /// <c>WOLVERINE_*</c> names stay until the CI harness (build/SupervisedTests.cs) moves over.
    /// </para>
    /// </remarks>
    private static string From(string name, string variable, string fallback)
    {
        // Treat blank as unset: an exported-but-empty variable is a misconfiguration, and silently
        // handing an empty connection string to a driver produces a far worse error message.
        foreach (var candidate in new[] { "ConnectionStrings__" + name, variable })
        {
            var value = Environment.GetEnvironmentVariable(candidate);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return fallback;
    }

    public static readonly string PostgresConnectionString =
        From("postgres", "WOLVERINE_POSTGRES", "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres");

    /// <summary>
    /// The database name inside <see cref="PostgresConnectionString"/> — "postgres" unless
    /// ConnectionStrings__postgres or WOLVERINE_POSTGRES points somewhere else, as each worker lane does under parallelized CI.
    /// Assertions about database identity (durability agent URIs, subscription URIs, tenant
    /// master checks) must build on this rather than the literal "postgres", or they fail against
    /// any overridden database while testing nothing extra against the default one.
    /// </summary>
    /// <remarks>
    /// Parsed by hand because this file is linked into every test project, including ones that
    /// do not reference Npgsql.
    /// </remarks>
    public static string PostgresDatabaseName =>
        PostgresConnectionString.Split(';')
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Database", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .LastOrDefault() ?? "postgres";

    public static readonly string SqlServerConnectionString =
        From("sqlserver", "WOLVERINE_SQLSERVER",
            "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False");

    /// <summary>
    /// The catalog inside <see cref="SqlServerConnectionString"/> — "master" unless
    /// ConnectionStrings__sqlserver or WOLVERINE_SQLSERVER points somewhere else, as each worker lane does under parallelized CI.
    /// Same contract as <see cref="PostgresDatabaseName"/>: derive database-identity assertions
    /// and sibling-database names from this, never from the literal default.
    /// </summary>
    public static string SqlServerDatabaseName =>
        SqlServerConnectionString.Split(';')
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .LastOrDefault() ?? "master";

    public static readonly string MySqlConnectionString =
        From("mysql", "WOLVERINE_MYSQL", "Server=localhost;Port=3306;Database=wolverine;User=root;Password=P@55w0rd;");

    public static readonly string OracleConnectionString =
        From("oracle", "WOLVERINE_ORACLE", "User Id=wolverine;Password=wolverine;Data Source=localhost:1521/FREEPDB1");

    public static readonly string AzureServiceBusConnectionString =
        From("azureservicebus", "WOLVERINE_AZURE_SERVICE_BUS",
            "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusManagementConnectionString =
        From("azureservicebus-management", "WOLVERINE_AZURE_SERVICE_BUS_MANAGEMENT",
            "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusTenantConnectionString =
        From("azureservicebus-tenant", "WOLVERINE_AZURE_SERVICE_BUS_TENANT",
            "Endpoint=sb://localhost:5674;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusTenantManagementConnectionString =
        From("azureservicebus-tenant-management", "WOLVERINE_AZURE_SERVICE_BUS_TENANT_MANAGEMENT",
            "Endpoint=sb://localhost:5301;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
}
