namespace IntegrationTests;

public class Servers
{
    /// <summary>
    /// The connection string for <paramref name="variable"/>, or <paramref name="fallback"/> when
    /// that environment variable is unset or blank.
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
    /// </remarks>
    private static string From(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);

        // Treat blank as unset: an exported-but-empty variable is a misconfiguration, and silently
        // handing an empty connection string to a driver produces a far worse error message.
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    public static readonly string PostgresConnectionString =
        From("WOLVERINE_POSTGRES", "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres");

    public static readonly string SqlServerConnectionString =
        From("WOLVERINE_SQLSERVER",
            "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False");

    public static readonly string MySqlConnectionString =
        From("WOLVERINE_MYSQL", "Server=localhost;Port=3306;Database=wolverine;User=root;Password=P@55w0rd;");

    public static readonly string OracleConnectionString =
        From("WOLVERINE_ORACLE", "User Id=wolverine;Password=wolverine;Data Source=localhost:1521/FREEPDB1");

    public static readonly string AzureServiceBusConnectionString =
        From("WOLVERINE_AZURE_SERVICE_BUS",
            "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusManagementConnectionString =
        From("WOLVERINE_AZURE_SERVICE_BUS_MANAGEMENT",
            "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusTenantConnectionString =
        From("WOLVERINE_AZURE_SERVICE_BUS_TENANT",
            "Endpoint=sb://localhost:5674;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

    public static readonly string AzureServiceBusTenantManagementConnectionString =
        From("WOLVERINE_AZURE_SERVICE_BUS_TENANT_MANAGEMENT",
            "Endpoint=sb://localhost:5301;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
}
