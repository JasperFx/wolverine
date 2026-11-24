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

                // GH-5760. The tests drive the durability agent's recovery pass (moving replayable dead letters to the
                // inbox) themselves. The agent's own pass moved replayed dead letters mid-test, so a discard or count
                // that followed a replay saw fewer rows than it replayed. Its first pass now comes long after any test
                // here; RavenDB's twin removes its replayer instead
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromHours(1);
            }).StartAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
