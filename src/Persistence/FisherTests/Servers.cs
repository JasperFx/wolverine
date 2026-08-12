namespace FisherTests;

/// <summary>
///     A Fisher store is a SQLite <b>file</b>, not a server, so there is no connection string in
///     <c>src/Servers.cs</c> to reach for. Each fixture gets its own file, which is also what keeps
///     concurrently-running test classes from becoming two writers on one database — the failure mode
///     Fisher's own docs call out as presenting like a hang rather than an error.
/// </summary>
public static class Servers
{
    private static readonly string _databaseDirectory =
        Path.Combine(Directory.GetCurrentDirectory(), ".wolverine", "fisher");

    public static FisherTestDatabase CreateDatabase(string prefix = "fisher_test")
    {
        Directory.CreateDirectory(_databaseDirectory);

        var databaseFile = Path.Combine(_databaseDirectory, $"{prefix}_{Guid.NewGuid():N}.db");
        return new FisherTestDatabase(databaseFile);
    }

    internal static void CleanupDatabaseFiles(string databaseFile)
    {
        deleteIfExists(databaseFile);

        // WAL has to stay on for the async daemon, so the sidecar files are always in play
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
            // Ignore cleanup failures while SQLite still holds file locks
        }
    }
}

public sealed class FisherTestDatabase : IDisposable
{
    internal FisherTestDatabase(string databaseFile)
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
