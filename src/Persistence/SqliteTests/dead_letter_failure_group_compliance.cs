using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

public class dead_letter_failure_group_compliance : RelationalDeadLetterFailureGroupCompliance, IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(dead_letter_failure_group_compliance));

    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
