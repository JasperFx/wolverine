namespace SqliteTests;

public static class Servers
{
    // Generate unique in-memory database connection string for each test
    public static string CreateInMemoryConnectionString() =>
        $"Data Source=test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
}
