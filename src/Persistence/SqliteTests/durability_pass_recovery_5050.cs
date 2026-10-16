using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-5050 on SQLite. See <see cref="DurabilityPassRecoveryCompliance" />
/// </summary>
[Collection("sqlite")]
public class durability_pass_recovery_5050 : DurabilityPassRecoveryCompliance
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(durability_pass_recovery_5050));

    protected override string SchemaName => "gh5050_pass";

    protected override void ConfigurePersistence(WolverineOptions opts, string schemaName)
    {
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    // The same text format as the datetime('now') column default, so the stale comparison agrees with it
    protected override object TimestampValue(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
