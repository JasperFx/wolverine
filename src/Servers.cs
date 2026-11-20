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
    /// The fallbacks are the docker-compose servers, so a developer or CI job that sets nothing needs
    /// nothing more than <c>docker compose up -d</c>. The relational ones name the 6.x
    /// <see cref="DefaultDatabase"/> rather than a database docker-compose made (GH-5635).
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

    /// <summary>
    /// Every database the 6.x test suites create or assume by a fixed name starts with this (GH-5635).
    /// The 6.x (<c>6.0</c> branch) and 7.x (<c>main</c>) suites share the docker-compose Postgres, SQL Server
    /// and MySQL servers, and one line's schema left in a database the other line also uses breaks the
    /// other line's next run. With the prefix, a 6.x run never opens a database a 7.x run made.
    /// </summary>
    /// <remarks>
    /// Name a sibling database (a tenant database, an ancillary store's database, a MySQL schema) with
    /// <see cref="DatabaseName"/>, never with a bare literal.
    /// </remarks>
    public const string DatabasePrefix = "wolverine6_";

    /// <summary>
    /// The database the suites use when nothing overrides the connection string, on every server.
    /// Created on first use (docker-compose only creates <c>postgres</c>, <c>master</c> and MySQL's <c>wolverine</c>).
    /// </summary>
    public const string DefaultDatabase = DatabasePrefix + "testing";

    /// <summary>
    /// <paramref name="name"/> with the 6.x <see cref="DatabasePrefix"/>, for any database a test creates
    /// or connects to beside the one its connection string names. A name that already carries the prefix
    /// is returned as it is.
    /// </summary>
    public static string DatabaseName(string name)
        => name.StartsWith(DatabasePrefix, StringComparison.OrdinalIgnoreCase) ? name : DatabasePrefix + name;

    private static readonly Lazy<string> _postgres = new(() => FromOrDefaultDatabase("postgres", "WOLVERINE_POSTGRES",
        "Host=localhost;Port=5433;Database={0};Username=postgres;password=postgres", "postgres",
        "Npgsql.NpgsqlFactory, Npgsql",
        $"select count(*) from pg_database where datname = '{DefaultDatabase}'",
        $"create database {DefaultDatabase}"));

    public static string PostgresConnectionString => _postgres.Value;

    /// <summary>
    /// The database name inside <see cref="PostgresConnectionString"/> — <see cref="DefaultDatabase"/> unless
    /// ConnectionStrings__postgres or WOLVERINE_POSTGRES points somewhere else, as each worker lane does under parallelized CI.
    /// Assertions about database identity (durability agent URIs, subscription URIs, tenant
    /// master checks) must build on this rather than a literal, or they fail against
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
            .LastOrDefault() ?? DefaultDatabase;

    private static readonly Lazy<string> _sqlServer = new(() => FromOrDefaultDatabase("sqlserver", "WOLVERINE_SQLSERVER",
        "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog={0};Encrypt=False",
        "master",
        "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient",
        $"select count(*) from sys.databases where name = '{DefaultDatabase}'",
        $"create database [{DefaultDatabase}]"));

    public static string SqlServerConnectionString => _sqlServer.Value;

    /// <summary>
    /// The catalog inside <see cref="SqlServerConnectionString"/> — <see cref="DefaultDatabase"/> unless
    /// ConnectionStrings__sqlserver or WOLVERINE_SQLSERVER points somewhere else, as each worker lane does under parallelized CI.
    /// Same contract as <see cref="PostgresDatabaseName"/>: derive database-identity assertions
    /// and sibling-database names from this, never from a literal.
    /// </summary>
    public static string SqlServerDatabaseName =>
        SqlServerConnectionString.Split(';')
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Trim().Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1].Trim())
            .LastOrDefault() ?? DefaultDatabase;

    private static readonly Lazy<string> _mySql = new(() => FromOrDefaultDatabase("mysql", "WOLVERINE_MYSQL",
        "Server=localhost;Port=3306;Database={0};User=root;Password=P@55w0rd;", "mysql",
        "MySqlConnector.MySqlConnectorFactory, MySqlConnector",
        $"select count(*) from information_schema.schemata where schema_name = '{DefaultDatabase}'",
        $"create database if not exists {DefaultDatabase}"));

    public static string MySqlConnectionString => _mySql.Value;

    /// <summary>
    /// <see cref="From"/>, where the docker-compose fallback names <see cref="DefaultDatabase"/>: when nothing
    /// overrides the connection string, that database is created the first time it is asked for, through the
    /// ADO.NET provider the test project already references. A project without the provider, or a server that
    /// is not up, skips the step; the test's own connection then fails with the driver's usual error.
    /// </summary>
    /// <remarks>
    /// By reflection because this file is linked into every test project, including ones that reference
    /// none of these drivers.
    /// </remarks>
    private static string FromOrDefaultDatabase(string name, string variable, string fallbackFormat,
        string adminDatabase, string factoryType, string existsSql, string createSql)
    {
        var fallback = string.Format(fallbackFormat, DefaultDatabase);
        var connectionString = From(name, variable, fallback);
        if (!ReferenceEquals(connectionString, fallback)) return connectionString;

        try
        {
            createDefaultDatabase(factoryType, string.Format(fallbackFormat, adminDatabase), existsSql, createSql);
        }
        catch (Exception)
        {
            // Not this method's error to report. See the summary.
        }

        return connectionString;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Test infrastructure; a provider the trimmer removed just skips the step")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "Test infrastructure; a provider the trimmer removed just skips the step")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Test infrastructure; a provider the trimmer removed just skips the step")]
    private static void createDefaultDatabase(string factoryType, string adminConnectionString, string existsSql,
        string createSql)
    {
        var factory = Type.GetType(factoryType, throwOnError: false)
            ?.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as System.Data.Common.DbProviderFactory;
        if (factory == null) return;

        // Several worker processes start at once, and two CREATE DATABASE statements racing on one server can
        // fail on either side (Postgres also refuses one while another copies template1), so check again
        // after a failure and try again.
        for (var attempt = 0; ; attempt++)
        {
            using var conn = factory.CreateConnection()!;
            conn.ConnectionString = adminConnectionString;
            conn.Open();

            using var exists = conn.CreateCommand();
            exists.CommandText = existsSql;
            if (Convert.ToInt64(exists.ExecuteScalar()) > 0) return;

            try
            {
                using var create = conn.CreateCommand();
                create.CommandText = createSql;
                create.ExecuteNonQuery();
                return;
            }
            catch (System.Data.Common.DbException) when (attempt < 5)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
        }
    }

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
