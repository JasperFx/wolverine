using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4863, reported by @alexandrefresnais.
///
/// <para>
/// When a global partition slot moves, GH-4777 releases the losing node's un-executed companion backlog to
/// the inbox and GH-4776's companion recovery loop picks it up on the new owner. The page query that reads
/// those rows carries no ORDER BY -- and no ORDER BY would be reliable, since the envelope id generator is
/// user-replaceable and its default sorts differently on every database -- so the new owner enqueued the
/// backlog in whatever order the heap scan returned it after the release update. Messages sharing a group
/// id, which the previous owner had been running strictly in sequence, then ran on the gaining node in an
/// arbitrary order: 20, 25, 30, 21, 26, ... in the report.
/// </para>
///
/// <para>
/// The fix orders each sweep's recovered rows by <c>Envelope.SentAt</c> before enqueueing, and accumulates
/// the sweep's pages first so the order holds across pages too. A Solo host gives the same deterministic
/// handoff as the GH-4777 fixture: stopping the slot's listener releases the backlog, restarting it recovers
/// the backlog on the "new owner". The page size is forced small so the backlog spans several pages.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_4863_recovered_backlog_runs_in_sent_order : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        OrderedSlotHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                // Small pages so a thirty message backlog is recovered across several of them. Before the
                // fix each page was enqueued in query order as soon as it was read, so even a correctly
                // ordered page could not keep the backlog in sequence across page boundaries.
                opts.Durability.RecoveryBatchSize = 5;

                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "slot4863",
                        transportSchema: "slot4863_queues")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(OrderedSlotHandler));

                opts.MessagePartitioning.ByMessage<OrderedSlotMessage>(x => x.GroupId.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedPostgresqlQueues("orderedslot", 2);
                    topology.Message<OrderedSlotMessage>();
                });
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _store = _host.Services.GetRequiredService<IMessageStore>();

        // Same reasoning as the GH-4777 fixture: AutoPurgeOnStartup clears the queue tables, not the inbox,
        // and the companion recovery loop would execute a previous run's leftovers into this run's sequence.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var delete = conn.CreateCommand();
        delete.CommandText = "delete from slot4863.wolverine_incoming_envelopes";
        await delete.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        OrderedSlotHandler.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        OrderedSlotHandler.Reset();
        await _host.StopAsync();
        _host.Dispose();
    }

    private PostgresqlQueue theSlot() =>
        _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["orderedslot1"];

    private Guid groupIdLandingOn(Uri companion)
    {
        var bus = _host.MessageBus();

        for (var i = 0; i < 500; i++)
        {
            var candidate = Guid.NewGuid();
            var destination = bus.PreviewSubscriptions(new OrderedSlotMessage(candidate, 0)).Single().Destination;
            if (destination == companion) return candidate;
        }

        throw new TimeoutException($"Could not find a group id routing to {companion}");
    }

    [Fact]
    public async Task the_backlog_resumes_on_the_new_owner_in_the_order_it_was_sent()
    {
        var slot = theSlot();
        var companion = slot.GlobalPartitionLocalQueueUri!;
        var groupId = groupIdLandingOn(companion);

        var bus = _host.MessageBus();
        const int total = 30;
        for (var i = 1; i <= total; i++)
        {
            await bus.PublishAsync(new OrderedSlotMessage(groupId, i));
        }

        // Let a few through so there is a real backlog to hand over
        await waitForAsync(() => OrderedSlotHandler.Started.Count >= 3,
            () => $"The backlog never started. started={OrderedSlotHandler.Started.Count}");

        // The losing node's half of the move: the companion backlog is released to the inbox
        await _runtime.Endpoints.StopListenerAsync(slot, TestContext.Current.CancellationToken);
        var atStop = OrderedSlotHandler.Started.ToArray();
        atStop.Length.ShouldBeLessThan(total - 10, "Precondition: most of the backlog has to be left to recover");

        (await _store.LoadPageOfGloballyOwnedIncomingAsync(companion, total * 2)).Count
            .ShouldBeGreaterThan(_runtime.DurabilitySettings.RecoveryBatchSize,
                "Precondition: the released backlog has to span more than one recovery page");

        // The gaining node's half: re-acquiring the slot recovers the backlog
        await _runtime.Endpoints.StartListenerAsync(slot, TestContext.Current.CancellationToken);

        var expected = Enumerable.Range(1, total).ToArray();
        await waitForAsync(() => expected.All(OrderedSlotHandler.Started.Contains),
            () => "The released backlog was never finished after the slot came back. missing=" +
                  $"[{string.Join(", ", expected.Where(n => !OrderedSlotHandler.Started.Contains(n)))}]");

        // The order the recovered messages STARTED, first occurrence of each (a message that was in flight
        // at the drain boundary may legitimately run twice; see the GH-4777 fixture). Before the fix this
        // read like 20, 25, 30, 21, 26, 22, ... -- the page query's order, not the group's.
        var recovered = OrderedSlotHandler.Started.ToArray()
            .Skip(atStop.Length)
            .Distinct()
            .Where(n => !atStop.Contains(n))
            .ToArray();

        recovered.ShouldBe(recovered.OrderBy(n => n).ToArray(),
            "A slot's backlog has to resume on its new owner in the order it was sent. Observed start order: " +
            $"[{string.Join(", ", recovered)}]");
    }

    private static async Task waitForAsync(Func<bool> condition, Func<string> diagnostic)
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds());
        }

        throw new TimeoutException(diagnostic());
    }
}

public record OrderedSlotMessage(Guid GroupId, int Number);

public static class OrderedSlotHandler
{
    /// <summary>
    /// The order messages STARTED, which is what the group ordering guarantee is about. A queue, because the
    /// assertion is on sequence.
    /// </summary>
    public static readonly ConcurrentQueue<int> Started = new();

    public static void Reset() => Started.Clear();

    public static async Task Handle(OrderedSlotMessage message)
    {
        Started.Enqueue(message.Number);
        await Task.Delay(150.Milliseconds());
    }
}
