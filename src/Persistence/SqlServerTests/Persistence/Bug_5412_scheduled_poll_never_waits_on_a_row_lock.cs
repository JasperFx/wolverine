using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

/// <summary>
/// GH-5412. The scheduled-message poll read the inbox under shared locks and waited on any row another
/// transaction held. Handlers marking their own rows Handled lock the same pages in the other order, so under
/// ordinary multi-node load the poll, or a handler's commit, was chosen as a deadlock victim several times a
/// minute. The poll now skips a locked row and leaves it for the next poll, so it can never be part of a cycle.
/// </summary>
public class Bug_5412_scheduled_poll_never_waits_on_a_row_lock
{
    private const string Schema = "gh5412";

    [Fact]
    public async Task a_locked_scheduled_row_is_skipped_and_the_rest_are_promoted()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(Schema, TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, Schema);
            }).StartAsync(TestContext.Current.CancellationToken);

        var store = host.Services.GetRequiredService<IMessageStore>();

        // Two scheduled envelopes that are both due
        var locked = await scheduleAsync(store);
        var free = await scheduleAsync(store);

        // Another transaction holds the first one, as a handler's commit would
        await using var holder = new SqlConnection(Servers.SqlServerConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"update {Schema}.{DatabaseConstants.IncomingTable} set attempts = attempts where id = @id";
            cmd.Parameters.AddWithValue("id", locked.Id);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Before the fix the poll waited on that lock until this timeout
        using var timeout = new CancellationTokenSource(10.Seconds());
        var runtime = Substitute.For<IWolverineRuntime>();
        await ((IMessageDatabase)store).PollForScheduledMessagesAsync(runtime, NullLogger.Instance,
            host.Services.GetRequiredService<DurabilitySettings>(), timeout.Token);

        await tx.RollbackAsync(TestContext.Current.CancellationToken);

        await runtime.Received().EnqueueDirectlyAsync(Arg.Is<IReadOnlyList<Envelope>>(x =>
            x.Count == 1 && x[0].Id == free.Id));

        var inbox = await store.Admin.AllIncomingAsync();
        inbox.Single(x => x.Id == free.Id).Status.ShouldBe(EnvelopeStatus.Incoming);

        // Still there for the next poll
        inbox.Single(x => x.Id == locked.Id).Status.ShouldBe(EnvelopeStatus.Scheduled);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Envelope> scheduleAsync(IMessageStore store)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);

        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.ScheduleExecutionAsync(envelope);

        return envelope;
    }
}
