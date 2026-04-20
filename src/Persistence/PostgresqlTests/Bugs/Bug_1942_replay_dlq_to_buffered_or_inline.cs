using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace PostgresqlTests.Bugs;

/// <summary>
/// GH-1942: When a non-durable endpoint (BufferedInMemory or Inline mode) persists a
/// failed message to the database-backed DLQ, marking the dead-letter row as replayable
/// causes the row to be moved back to wolverine_incoming. The durability agent picks
/// it up and dispatches it to the listener; the handler runs (and may even succeed),
/// but the inbox row is never marked Handled in the local-queue path because
/// BufferedReceiver.CompleteAsync is a no-op and BufferedLocalQueue.EnqueueDirectlyAsync
/// does not wrap the channel callback the way ListeningAgent.EnqueueDirectlyAsync does
/// for transport-backed endpoints. The result: the row sits in wolverine_incoming forever
/// and gets reprocessed every time ownership is reset (e.g. on every host restart).
///
/// These tests cover the two flavors of non-durable endpoint:
///   1. A local queue with .BufferedInMemory()              — currently broken (this PR fixes)
///   2. A RabbitMQ listener with .ProcessInline()           — works (covered by GH-1594 fix in
///                                                            ListeningAgent.EnqueueDirectlyAsync)
/// </summary>
public class Bug_1942_replay_dlq_to_buffered_or_inline : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public Bug_1942_replay_dlq_to_buffered_or_inline(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        Bug1942Handler.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task buffered_local_queue_replay_does_not_loop()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug1942_buffered");
                opts.Durability.Mode = DurabilityMode.Solo;

                // The local queue handling Bug1942Message uses the buffered (non-durable) receiver,
                // but the host still has a database message store, so failures land in the DB DLQ.
                opts.LocalQueueFor<Bug1942Message>().BufferedInMemory();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await runReplayScenarioAsync(host, "bug1942_buffered");
    }

    [Fact]
    public async Task inline_rabbitmq_listener_replay_does_not_loop()
    {
        var queueName = $"bug1942-inline-{Guid.NewGuid()}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug1942_inline");
                opts.Durability.Mode = DurabilityMode.Solo;

                // Disable Rabbit's native DLQ so failures land in the database-backed DLQ.
                opts.UseRabbitMq()
                    .DisableDeadLetterQueueing()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug1942Message>().ToRabbitQueue(queueName);
                opts.ListenToRabbitQueue(queueName).ProcessInline();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await runReplayScenarioAsync(host, "bug1942_inline");
    }

    private async Task runReplayScenarioAsync(IHost host, string schema)
    {
        // 1) Send a message that will fail on first attempt.
        Bug1942Handler.FailNext = true;

        await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new Bug1942Message(Guid.NewGuid()));

        var store = host.Services.GetRequiredService<IMessageStore>();

        // 2) Wait for the dead-letter row to materialize in the database.
        var deadLetterId = await waitForDeadLetterAsync(store);
        deadLetterId.ShouldNotBeNull("Failed message should land in the database DLQ");

        // 3) Allow the handler to succeed on the next attempt, then mark the row replayable.
        //    The durability agent should move the row back to incoming and re-dispatch it.
        Bug1942Handler.FailNext = false;

        var tracked = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<Bug1942Message>(host)
            .ExecuteAndWaitAsync((IMessageContext _) =>
                store.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(new[] { deadLetterId.Value }));

        tracked.MessageSucceeded.SingleMessage<Bug1942Message>()
            .ShouldNotBeNull("Replayed message should be processed successfully on retry");

        // 4) The bug: the row is left in wolverine_incoming after a successful replay because
        //    BufferedReceiver/InlineReceiver never call MarkIncomingEnvelopeAsHandledAsync when
        //    dispatched via the local-queue path. We poll briefly to give any async cleanup
        //    a chance to run, then assert the inbox is drained.
        var sw = Stopwatch.StartNew();
        PersistedCounts counts;
        do
        {
            counts = await store.Admin.FetchCountsAsync();
            _output.WriteLine($"[POLL] DLQ={counts.DeadLetter}, Incoming={counts.Incoming}, Handled={counts.Handled}");
            if (counts.Incoming == 0) break;
            await Task.Delay(250);
        } while (sw.Elapsed < TimeSpan.FromSeconds(5));

        if (counts.Incoming != 0)
        {
            _output.WriteLine($"[STUCK] Inbox rows after replay (handler invocations: {Bug1942Handler.InvocationCount}):");
            await dumpIncomingRowsAsync(schema);
        }

        counts.DeadLetter.ShouldBe(0);
        counts.Incoming.ShouldBe(0);
    }

    private static async Task<Guid?> waitForDeadLetterAsync(IMessageStore store)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            var page = await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery { PageSize = 10 },
                CancellationToken.None);
            if (page.Envelopes.Any())
            {
                return page.Envelopes.First().Id;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private async Task dumpIncomingRowsAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select id, status, owner_id, message_type, received_at, attempts from {schema}.wolverine_incoming_envelopes";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            _output.WriteLine(
                $"  [INCOMING] id={reader.GetGuid(0)} status={reader.GetString(1)} owner_id={reader.GetInt32(2)} message_type={reader.GetString(3)} received_at={reader.GetString(4)} attempts={reader.GetInt32(5)}");
        }
    }
}

public record Bug1942Message(Guid Id);

public static class Bug1942Handler
{
    public static bool FailNext;
    public static int InvocationCount;

    public static void Reset()
    {
        FailNext = false;
        InvocationCount = 0;
    }

    public static void Handle(Bug1942Message _)
    {
        Interlocked.Increment(ref InvocationCount);
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("Bug 1942 forced failure");
        }
    }
}
