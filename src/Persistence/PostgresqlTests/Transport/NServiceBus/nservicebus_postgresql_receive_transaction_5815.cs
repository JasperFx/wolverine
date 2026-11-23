using System.Collections.Concurrent;
using System.Threading.Channels;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport.NServiceBus;
using Wolverine.Transports;

namespace PostgresqlTests.Transport.NServiceBus;

// GH-5815. An Inline NServiceBus PostgreSQL listener pops with an autocommitted DELETE ... RETURNING, so the row is gone
// while the handler runs: a crash mid-handler loses the message, and a deferral can only re-insert it behind newer
// messages. UseReceiveTransaction() pops inside a transaction that stays open until the message is settled.
public class nservicebus_postgresql_receive_transaction_5815 : IAsyncLifetime
{
    private readonly string _queueName = "nsb_rtx_" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;
    private NServiceBusPostgresqlQueue _queue = null!;
    private IWolverineRuntime _runtime = null!;
    private readonly List<NServiceBusPostgresqlQueueListener> _listeners = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbrtx");
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                opts.PublishMessage<PgNsbReceiveTxPing>().ToNServiceBusPostgresqlQueue(_queueName);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _queue = _runtime.Options.Transports.GetOrCreate<NServiceBusPostgresqlTransport>().Queues[_queueName];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners) await listener.DisposeAsync();

        await _host.StopAsync();
        _host.Dispose();
    }

    private string table => _queue.TableIdentifier.QualifiedName;

    private static async Task<NpgsqlConnection> openAsync()
    {
        var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<long> countAsync(Guid id)
    {
        await using var conn = await openAsync();
        await using var cmd = new NpgsqlCommand($"select count(*) from {table} where id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // The ids in the table, in the order NServiceBus (and the listener) receives them
    private async Task<List<(Guid Id, long Seq)>> rowsAsync()
    {
        await using var conn = await openAsync();
        await using var cmd = new NpgsqlCommand($"select id, seq from {table} order by seq", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<(Guid, long)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), Convert.ToInt64(reader.GetValue(1))));
        return rows;
    }

    // Is some row in the table held by an open transaction? Such a row is still visible to a plain read, but a
    // locking read skips it
    private async Task<bool> tableIsLockedAsync()
    {
        await using var conn = await openAsync();
        await using var cmd = new NpgsqlCommand(
            $"select (select count(*) from {table}) - (select count(*) from (select id from {table} for update skip locked) x)",
            conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private async Task<Guid> sendAsync(string name)
    {
        var before = (await rowsAsync()).Count;
        var id = Guid.NewGuid();
        await _host.MessageBus().SendAsync(new PgNsbReceiveTxPing(id, name, false));

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while ((await rowsAsync()).Count == before)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message never reached the queue table");
            await Task.Delay(50);
        }

        return (await rowsAsync()).Last().Id;
    }

    private async Task<(NServiceBusPostgresqlQueueListener, PgGatedReceiver5815)> listenAsync(bool receiveTransaction)
    {
        _queue.Mode = EndpointMode.Inline;
        _queue.UseReceiveTransaction = receiveTransaction;
        _queue.PollingInterval = 100.Milliseconds();

        var receiver = new PgGatedReceiver5815();
        var listener = new NServiceBusPostgresqlQueueListener(_queue, _runtime, receiver);
        _listeners.Add(listener);
        await listener.StartAsync();
        return (listener, receiver);
    }

    private static async Task eventuallyAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (!await condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, because);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task a_completed_message_is_deleted()
    {
        var a = await sendAsync("a");
        var (listener, receiver) = await listenAsync(true);

        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);

        // In flight, the row is still there, held by the receive transaction
        (await tableIsLockedAsync()).ShouldBeTrue();
        (await countAsync(a)).ShouldBe(1);

        await listener.StopAsync();
        receiver.Release();
        await receiver.NextSettledAsync();

        await eventuallyAsync(async () => !await tableIsLockedAsync(), "the receive transaction never ended");
        (await countAsync(a)).ShouldBe(0);
        receiver.SettleFailures.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_deferred_message_keeps_its_place_at_the_head_of_the_queue()
    {
        var a = await sendAsync("a");
        var b = await sendAsync("b");
        var before = await rowsAsync();

        var (listener, receiver) = await listenAsync(true);
        receiver.Settle = (l, e) => l.DeferAsync(e);

        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);
        receiver.Release();

        // Rolled back to the head of the queue, so it is received again before b
        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);

        await listener.StopAsync();
        receiver.Release();
        await eventuallyAsync(async () => !await tableIsLockedAsync(), "the receive transaction never ended");

        // Not a copy: the same row, seq and all
        (await rowsAsync()).ShouldBe(before);
        receiver.SettleFailures.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_receiver_that_throws_leaves_the_message_in_place()
    {
        var a = await sendAsync("a");
        await sendAsync("b");
        var before = await rowsAsync();

        var (listener, receiver) = await listenAsync(true);
        receiver.Settle = (_, _) => ValueTask.CompletedTask;
        receiver.ThrowAfterSettling = true;

        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);
        await listener.StopAsync();
        receiver.Release();

        await eventuallyAsync(async () => !await tableIsLockedAsync(), "the receive transaction never ended");
        (await rowsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task a_session_that_dies_mid_handler_puts_the_message_back_in_place()
    {
        var a = await sendAsync("a");
        await sendAsync("b");
        var before = await rowsAsync();

        var (listener, receiver) = await listenAsync(true);
        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);
        await listener.StopAsync();

        // The process holding the receive transaction dies: the server ends its session
        await using (var conn = await openAsync())
        {
            await using var cmd = new NpgsqlCommand(
                $"select count(pg_terminate_backend(pid)) from pg_stat_activity where pid <> pg_backend_pid() and state like 'idle in transaction%' and query like '%{_queueName}%'",
                conn);
            Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        }

        await eventuallyAsync(async () => !await tableIsLockedAsync(), "the receive transaction never ended");
        (await rowsAsync()).ShouldBe(before);

        receiver.Release();
    }

    [Fact]
    public async Task disposing_the_listener_mid_handler_hands_the_message_back()
    {
        var a = await sendAsync("a");
        var before = await rowsAsync();

        var (listener, receiver) = await listenAsync(true);
        (await receiver.NextArrivalAsync()).Single().Id.ShouldBe(a);

        await listener.DisposeAsync();
        (await tableIsLockedAsync()).ShouldBeFalse();
        (await rowsAsync()).ShouldBe(before);

        // The handler finishing afterwards has nothing left to commit
        receiver.Release();
        await receiver.NextSettledAsync();
        (await rowsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task messages_are_received_one_at_a_time()
    {
        _queue.MaximumMessagesToReceive = 20;
        var a = await sendAsync("a");
        await sendAsync("b");
        await sendAsync("c");

        var (listener, receiver) = await listenAsync(true);
        (await receiver.NextArrivalAsync()).Select(x => x.Id).ShouldBe([a]);

        await listener.StopAsync();
        receiver.Release();
    }

    [Fact]
    public async Task without_the_opt_in_the_row_is_deleted_as_the_message_is_received()
    {
        _queue.MaximumMessagesToReceive = 20;
        var a = await sendAsync("a");
        var b = await sendAsync("b");

        var (listener, receiver) = await listenAsync(false);
        (await receiver.NextArrivalAsync()).Select(x => x.Id).ShouldBe([a, b]);

        // Gone while the handler is still running, and nothing holds a lock on it
        (await tableIsLockedAsync()).ShouldBeFalse();
        (await countAsync(a)).ShouldBe(0);

        await listener.StopAsync();
        receiver.Release();
        await receiver.NextSettledAsync();
        (await rowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_opt_in_is_refused_on_a_buffered_listener()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbrtx");
                    opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                    opts.ListenToNServiceBusPostgresqlQueue(_queueName).UseReceiveTransaction();

                    opts.Discovery.DisableConventionalDiscovery();
                }).StartAsync();
        });

        ex.Message.ShouldContain("UseReceiveTransaction() requires ProcessInline()");
    }

    [Fact]
    public async Task a_requeued_message_is_handled_again_before_the_messages_behind_it()
    {
        var run = Guid.NewGuid();
        await _host.MessageBus().SendAsync(new PgNsbReceiveTxPing(run, "a", true));
        await _host.MessageBus().SendAsync(new PgNsbReceiveTxPing(run, "b", false));
        await eventuallyAsync(async () => (await rowsAsync()).Count == 2, "the messages never reached the queue table");

        using var receiving = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbrtx");
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                #region sample_nservicebus_postgresql_receive_transaction
                // Keep each message's row, locked, until its handler is done with it
                opts.ListenToNServiceBusPostgresqlQueue(_queueName)
                    .ProcessInline()
                    .UseReceiveTransaction();
                #endregion

                opts.OnException<PgNsbReceiveTxFailure>().Requeue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<PgNsbReceiveTxPingHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        await eventuallyAsync(() => Task.FromResult(PgNsbReceiveTxPingHandler.HandledFor(run).Length == 3),
            "the messages were never all handled");

        // The deferral rolled a back to the head of the queue
        PgNsbReceiveTxPingHandler.HandledFor(run).ShouldBe(["a", "a", "b"]);
        await eventuallyAsync(async () => (await rowsAsync()).Count == 0, "the handled messages were never deleted");

        await receiving.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record PgNsbReceiveTxPing(Guid Run, string Name, bool FailFirst);

public class PgNsbReceiveTxFailure : Exception;

public class PgNsbReceiveTxPingHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _handled = new();

    public static string[] HandledFor(Guid run) =>
        _handled.TryGetValue(run, out var handled) ? handled.ToArray() : [];

    public static void Handle(PgNsbReceiveTxPing ping)
    {
        var handled = _handled.GetOrAdd(ping.Run, _ => new ConcurrentQueue<string>());
        var first = !handled.Contains(ping.Name);
        handled.Enqueue(ping.Name);

        if (ping.FailFirst && first) throw new PgNsbReceiveTxFailure();
    }
}

// Holds every delivery until the test releases it, then settles it the way the test says
internal class PgGatedReceiver5815 : IReceiver
{
    private readonly Channel<Envelope[]> _arrivals = Channel.CreateUnbounded<Envelope[]>();
    private readonly Channel<bool> _settled = Channel.CreateUnbounded<bool>();
    private readonly SemaphoreSlim _release = new(0);

    public Func<IListener, Envelope, ValueTask> Settle { get; set; } = (l, e) => l.CompleteAsync(e);

    public bool ThrowAfterSettling { get; set; }

    public ConcurrentQueue<Exception> SettleFailures { get; } = new();

    public void Release() => _release.Release();

    public async Task<Envelope[]> NextArrivalAsync()
    {
        using var timeout = new CancellationTokenSource(10.Seconds());
        return await _arrivals.Reader.ReadAsync(timeout.Token);
    }

    public async Task NextSettledAsync()
    {
        using var timeout = new CancellationTokenSource(10.Seconds());
        await _settled.Reader.ReadAsync(timeout.Token);
    }

    public async ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        await _arrivals.Writer.WriteAsync(messages);
        await _release.WaitAsync();

        foreach (var envelope in messages)
        {
            try
            {
                await Settle(listener, envelope);
            }
            catch (Exception e)
            {
                SettleFailures.Enqueue(e);
            }
        }

        await _settled.Writer.WriteAsync(true);

        if (ThrowAfterSettling) throw new InvalidOperationException("The receiver failed");
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope) => ReceivedAsync(listener, [envelope]);

    public ValueTask DrainAsync() => ValueTask.CompletedTask;

    public IHandlerPipeline Pipeline => null!;

    public void Dispose()
    {
    }
}
