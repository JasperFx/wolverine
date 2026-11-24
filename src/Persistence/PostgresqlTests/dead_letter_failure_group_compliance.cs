using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests;

public class dead_letter_failure_group_compliance : RelationalDeadLetterFailureGroupCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dlq_groups");
                opts.Durability.Mode = DurabilityMode.Solo;

                // GH-5760. The tests drive the durability agent's recovery pass (moving replayable dead letters to the
                // inbox) themselves. The agent's own pass moved replayed dead letters mid-test, so a discard or count
                // that followed a replay saw fewer rows than it replayed. Its first pass now comes long after any test
                // here; RavenDB's twin removes its replayer instead
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromHours(1);
            }).StartAsync();
    }

    protected override Task<IAsyncDisposable?> blockDeadLetterHistoryWritesAsync() =>
        holdLockAsync(
            $"lock table {((IMessageDatabase)thePersistence).TableNameFor(DatabaseConstants.DeadLetterHistoryTable)} in share mode");
}
