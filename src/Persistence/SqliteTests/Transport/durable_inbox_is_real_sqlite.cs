using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqliteTests.Transport;

/// <summary>
/// GH-4758. <see cref="SqliteQueue"/> implements <see cref="IDatabaseBackedEndpoint"/>, which tells
/// <c>DurableReceiver</c> that the transport has already written the incoming-envelope row and to skip its
/// own inbox INSERT. The SQLite listener did not keep that promise: it dequeued as
/// SELECT -> DELETE -> COMMIT -> hand to the receiver, with no inbox write anywhere in that transaction.
/// So there was a committed state in which the queue row was gone, no inbox row existed, and the only copy
/// of the message was in memory inside a running handler — losable on any process kill.
///
/// <para>These pin both halves of the fix: a durable listener has the envelope in the inbox before the
/// handler can run, and a buffered listener still dequeues destructively and writes nothing.</para>
/// </summary>
[Collection("durable_inbox_is_real_sqlite")]
public class durable_inbox_is_real_sqlite
{
    [Fact]
    public async Task the_queue_is_database_backed_and_durable_mode_is_really_durable()
    {
        using var database = Servers.CreateDatabase(nameof(durable_inbox_is_real_sqlite));
        BlockingSqliteQueueHandler.Reset();

        var host = await startHostAsync(database.ConnectionString, x => x.UseDurableInbox());

        try
        {
            var queue = queueFor(host);

            // The marker stays — the listener honours it now instead of the receiver compensating for it.
            queue.ShouldBeAssignableTo<IDatabaseBackedEndpoint>();
            queue.Mode.ShouldBe(EndpointMode.Durable);
        }
        finally
        {
            await stopHostAsync(host);
        }
    }

    [Fact]
    public async Task a_durable_dequeue_is_in_the_inbox_before_its_handler_runs()
    {
        using var database = Servers.CreateDatabase(nameof(durable_inbox_is_real_sqlite));
        BlockingSqliteQueueHandler.Reset();

        var host = await startHostAsync(database.ConnectionString, x => x.UseDurableInbox());

        try
        {
            var store = host.Services.GetRequiredService<IMessageStore>();
            var queue = queueFor(host);

            await sendAsync(host, new BlockingSqliteQueueMessage(Guid.NewGuid()));

            // The handler has started and is parked. Everything asserted below is a COMMITTED state that a
            // process kill right now would have to survive.
            await BlockingSqliteQueueHandler.Started.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

            (await queue.CountAsync()).ShouldBe(0,
                "the listener has already committed removal from the SQLite transport queue");

            var counts = await store.Admin.FetchCountsAsync();

            counts.Incoming.ShouldBe(1,
                "a durable SQLite dequeue must be recoverable from the inbox while its handler is still running");

            // ...and the row is retired once the handler finishes, so the transfer is not a leak
            BlockingSqliteQueueHandler.Release();

            await waitUntilAsync(async () => (await store.Admin.FetchCountsAsync()).Incoming == 0, 30.Seconds());
        }
        finally
        {
            BlockingSqliteQueueHandler.Release();
            await stopHostAsync(host);
        }
    }

    [Fact]
    public async Task a_buffered_dequeue_is_still_destructive_and_writes_nothing_to_the_inbox()
    {
        using var database = Servers.CreateDatabase(nameof(durable_inbox_is_real_sqlite));
        BlockingSqliteQueueHandler.Reset();

        var host = await startHostAsync(database.ConnectionString, x => x.BufferedInMemory());

        try
        {
            var store = host.Services.GetRequiredService<IMessageStore>();
            var queue = queueFor(host);

            queue.Mode.ShouldBe(EndpointMode.BufferedInMemory);

            await sendAsync(host, new BlockingSqliteQueueMessage(Guid.NewGuid()));

            await BlockingSqliteQueueHandler.Started.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

            (await queue.CountAsync()).ShouldBe(0, "the buffered dequeue is still destructive");

            var counts = await store.Admin.FetchCountsAsync();
            counts.Incoming.ShouldBe(0, "a buffered listener must not pay for durability it did not ask for");

            BlockingSqliteQueueHandler.Release();
        }
        finally
        {
            BlockingSqliteQueueHandler.Release();
            await stopHostAsync(host);
        }
    }

    /// <summary>
    /// The durable pop reads, deletes and inserts in ONE transaction. Microsoft.Data.Sqlite begins that
    /// transaction with BEGIN IMMEDIATE, so SQLite's single write lock is taken before the first statement
    /// and two listeners serialize rather than interleave — which is also why the absence of
    /// <c>FOR UPDATE SKIP LOCKED</c> costs nothing here. This pins the consequence: every message is handed
    /// out exactly once, and the inbox holds exactly one row per message.
    /// </summary>
    [Fact]
    public async Task concurrent_durable_listeners_neither_lose_nor_duplicate()
    {
        const int count = 30;

        using var database = Servers.CreateDatabase(nameof(durable_inbox_is_real_sqlite));

        // Publish-only: no listener of its own, so the two built below are the only things popping.
        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        try
        {
            var runtime = host.GetRuntime();
            var store = host.Services.GetRequiredService<IMessageStore>();
            var queue = queueFor(host);

            // Small batches so the two listeners contend over many rounds rather than one
            queue.MaximumMessagesToReceive = 3;

            for (var i = 0; i < count; i++)
            {
                await queue.SendAsync(ObjectMother.Envelope());
            }

            (await queue.CountAsync()).ShouldBe(count);

            await using var left = new SqliteQueueListener(queue, runtime, Substitute.For<IReceiver>(),
                queue.DataSource, null);
            await using var right = new SqliteQueueListener(queue, runtime, Substitute.For<IReceiver>(),
                queue.DataSource, null);

            // A SELECT that comes back empty is authoritative: a concurrent pop cannot be holding rows
            // half-way, because it would still have been blocked at BEGIN.
            async Task<List<Envelope>> drain(SqliteQueueListener listener)
            {
                var popped = new List<Envelope>();
                while (true)
                {
                    var batch = await listener.TryPopDurablyAsync(TestContext.Current.CancellationToken);
                    if (batch.Count == 0) return popped;

                    popped.AddRange(batch);
                }
            }

            var both = await Task.WhenAll(drain(left), drain(right));
            var all = both.SelectMany(x => x).ToArray();

            all.Length.ShouldBe(count);
            all.Select(x => x.Id).Distinct().Count().ShouldBe(count);

            (await queue.CountAsync()).ShouldBe(0);
            (await store.Admin.FetchCountsAsync()).Incoming.ShouldBe(count);
        }
        finally
        {
            await stopHostAsync(host);
        }
    }

    /// <summary>
    /// The anti-duplicate guard. A latched receiver defers an already-persisted envelope back onto the queue
    /// table while its inbox row is still there, so the next durable pop would insert a second row on the
    /// same primary key and roll its whole transaction back — forever. PostgreSQL and SQL Server drop such a
    /// row before their pop; so does this, and the surviving inbox row is the one that carries on.
    /// </summary>
    [Fact]
    public async Task a_queue_row_already_in_the_inbox_is_dropped_rather_than_inserted_twice()
    {
        using var database = Servers.CreateDatabase(nameof(durable_inbox_is_real_sqlite));

        var host = await startPublishOnlyHostAsync(database.ConnectionString);

        try
        {
            var runtime = host.GetRuntime();
            var store = host.Services.GetRequiredService<IMessageStore>();
            var queue = queueFor(host);

            var envelope = ObjectMother.Envelope();
            envelope.Destination = queue.Uri;
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = runtime.Options.Durability.AssignedNodeNumber;

            // Already in the inbox...
            await store.Inbox.StoreIncomingAsync(envelope);

            // ...and back on the queue, which is the state a deferred-while-latched envelope leaves behind
            await queue.SendAsync(envelope);

            (await queue.CountAsync()).ShouldBe(1);
            (await store.Admin.FetchCountsAsync()).Incoming.ShouldBe(1);

            await using var listener = new SqliteQueueListener(queue, runtime, Substitute.For<IReceiver>(),
                queue.DataSource, null);

            var popped = await listener.TryPopDurablyAsync(TestContext.Current.CancellationToken);

            popped.ShouldBeEmpty("the envelope is already in the inbox; the duplicate queue row is dropped");
            (await queue.CountAsync()).ShouldBe(0);
            (await store.Admin.FetchCountsAsync()).Incoming.ShouldBe(1, "and the inbox still holds exactly one row");
        }
        finally
        {
            await stopHostAsync(host);
        }
    }

    private static Task<IHost> startPublishOnlyHostAsync(string connectionString)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseSqlitePersistenceAndTransport(connectionString).AutoProvision();
                opts.PublishAllMessages().ToSqliteQueue(QueueName);
            }).StartAsync();
    }

    private static Task<IHost> startHostAsync(string connectionString,
        Action<SqliteListenerConfiguration> configureListener)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BlockingSqliteQueueHandler));

                opts.UseSqlitePersistenceAndTransport(connectionString).AutoProvision();

                var listener = opts.ListenToSqliteQueue(QueueName).PollingInterval(100.Milliseconds());
                configureListener(listener);
            }).StartAsync();
    }

    private const string QueueName = "durable_inbox_is_real";

    private static SqliteQueue queueFor(IHost host)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
    }

    private static ValueTask sendAsync(IHost host, BlockingSqliteQueueMessage message)
    {
        return host.MessageBus().EndpointFor($"sqlite://{QueueName}".ToUri()).SendAsync(message);
    }

    private static async Task stopHostAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time");
            }

            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }
    }
}

public record BlockingSqliteQueueMessage(Guid Id);

public static class BlockingSqliteQueueHandler
{
    private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource Started { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Release()
    {
        _gate.TrySetResult();
    }

    public static async Task Handle(BlockingSqliteQueueMessage message)
    {
        Started.TrySetResult();
        await _gate.Task;
    }
}
