namespace IntegrationTests;

public class Servers
{
    public static readonly string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

    public static readonly string SqlServerConnectionString =
        "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=master;Encrypt=False";
}
