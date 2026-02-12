namespace IntegrationTests;

public class Servers
{
    public static readonly string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

    public static readonly string SqlServerConnectionString =
        "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False";

    public static readonly string MySqlConnectionString =
        "Server=localhost;Port=3306;Database=wolverine;User=root;Password=P@55w0rd;";

    public static readonly string OracleConnectionString =
        "User Id=wolverine;Password=wolverine;Data Source=localhost:1521/FREEPDB1";
}