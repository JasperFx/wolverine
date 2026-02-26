namespace SqliteTests;

public static class Servers
{
    private static readonly string _databaseDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".wolverine", "sqlite");

    public static SqliteTestDatabase CreateDatabase(string prefix = "sqlite_test")
    {
        Directory.CreateDirectory(_databaseDirectory);

        var databaseFile = Path.Combine(_databaseDirectory, $"{prefix}_{Guid.NewGuid():N}.db");
        return new SqliteTestDatabase(databaseFile);
    }

    internal static void CleanupDatabaseFiles(string databaseFile)
    {
        deleteIfExists(databaseFile);
        deleteIfExists(databaseFile + "-wal");
        deleteIfExists(databaseFile + "-shm");
    }

    private static void deleteIfExists(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException)
        {
            // Ignore cleanup failures while SQLite handles file locks.
        }
    }
}

public sealed class SqliteTestDatabase : IDisposable
{
    internal SqliteTestDatabase(string databaseFile)
    {
        DatabaseFile = databaseFile;
        ConnectionString = $"Data Source={databaseFile}";
    }

    public string DatabaseFile { get; }
    public string ConnectionString { get; }

    public void Dispose()
    {
        Servers.CleanupDatabaseFiles(DatabaseFile);
    }
}
