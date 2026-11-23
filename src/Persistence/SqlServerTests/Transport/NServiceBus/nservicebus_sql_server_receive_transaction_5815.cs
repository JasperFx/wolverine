using System.Collections.Concurrent;
using System.Threading.Channels;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport.NServiceBus;
using Wolverine.Transports;

namespace SqlServerTests.Transport.NServiceBus;

// GH-5815. An Inline NServiceBus SQL Server listener pops with an autocommitted DELETE ... OUTPUT, so the row is gone
// while the handler runs: a crash mid-handler loses the message, and a deferral can only re-insert it behind newer
// messages. UseReceiveTransaction() pops inside a transaction that stays open until the message is settled.
public class nservicebus_sql_server_receive_transaction_5815 : IAsyncLifetime
{
    private readonly string _queueName = "nsb_rtx_" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;
    private NServiceBusSqlServerQueue _queue = null!;
    private IWolverineRuntime _runtime = null!;
    private readonly List<NServiceBusSqlServerQueueListener> _listeners = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "nsbrtx");
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                opts.PublishMessage<NsbReceiveTxPing>().ToNServiceBusSqlServerQueue(_queueName);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _queue = _runtime.Options.Transports.GetOrCreate<NServiceBusSqlServerTransport>().Queues[_queueName];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners) await listener.DisposeAsync();

        await _host.StopAsync();
        _host.Dispose();
    }

    private string table => _queue.TableIdentifier.QualifiedName;

    private static async Task<SqlConnection> openAsync()
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> countAsync(Guid id)
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand($"select count(*) from {table} where Id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // The ids in the table, in the order NServiceBus (and the listener) receives them
    private async Task<List<(Guid Id, long RowVersion)>> rowsAsync()
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand($"select Id, RowVersion from {table} order by RowVersion", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<(Guid, long)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
        return rows;
    }

    // Is some row in the table held by an open transaction? A plain read waits on it, so give up quickly
    private async Task<bool> tableIsLockedAsync()
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand($"set lock_timeout 200; select count(*) from {table}", conn);
        try
        {
            await cmd.ExecuteScalarAsync();
            return false;
        }
        catch (SqlException e) when (e.Number == 1222)
        {
            return true;
        }
    }

    private async Task<Guid> sendAsync(string name)
    {
        var before = (await rowsAsync()).Count;
        var id = Guid.NewGuid();
        await _host.MessageBus().SendAsync(new NsbReceiveTxPing(id, name, false));

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while ((await rowsAsync()).Count == before)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message never reached the queue table");
            await Task.Delay(50);
        }

        return (await rowsAsync()).Last().Id;
    }

    private async Task<(NServiceBusSqlServerQueueListener, GatedReceiver5815)> listenAsync(bool receiveTransaction)
    {
        _queue.Mode = EndpointMode.Inline;
        _queue.UseReceiveTransaction = receiveTransaction;
        _queue.PollingInterval = 100.Milliseconds();

        var receiver = new GatedReceiver5815();
        var listener = new NServiceBusSqlServerQueueListener(_queue, _runtime, receiver);
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

        // Not a copy: the same row, RowVersion and all
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
        await executeAsync($"""
            declare @session int;
            select top 1 @session = s.session_id
            from sys.dm_exec_sessions s
            join sys.dm_exec_connections c on c.session_id = s.session_id
            cross apply sys.dm_exec_sql_text(c.most_recent_sql_handle) t
            where s.session_id <> @@SPID and s.open_transaction_count > 0 and t.text like '%{_queueName}%';
            if @session is null throw 50000, 'no session holds the receive transaction', 1;
            declare @kill nvarchar(20) = 'kill ' + cast(@session as nvarchar(10));
            exec (@kill);
            """);

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
                    opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "nsbrtx");
                    opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                    opts.ListenToNServiceBusSqlServerQueue(_queueName).UseReceiveTransaction();

                    opts.Discovery.DisableConventionalDiscovery();
                }).StartAsync();
        });

        ex.Message.ShouldContain("UseReceiveTransaction() requires ProcessInline()");
    }

    [Fact]
    public async Task a_requeued_message_is_handled_again_before_the_messages_behind_it()
    {
        var run = Guid.NewGuid();
        await _host.MessageBus().SendAsync(new NsbReceiveTxPing(run, "a", true));
        await _host.MessageBus().SendAsync(new NsbReceiveTxPing(run, "b", false));
        await eventuallyAsync(async () => (await rowsAsync()).Count == 2, "the messages never reached the queue table");

        using var receiving = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "nsbrtx");
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                #region sample_nservicebus_sql_server_receive_transaction
                // Keep each message's row, locked, until its handler is done with it
                opts.ListenToNServiceBusSqlServerQueue(_queueName)
                    .ProcessInline()
                    .UseReceiveTransaction();
                #endregion

                opts.OnException<NsbReceiveTxFailure>().Requeue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NsbReceiveTxPingHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        await eventuallyAsync(() => Task.FromResult(NsbReceiveTxPingHandler.HandledFor(run).Length == 3),
            "the messages were never all handled");

        // The deferral rolled a back to the head of the queue. The re-insert it replaces put a behind b
        NsbReceiveTxPingHandler.HandledFor(run).ShouldBe(["a", "a", "b"]);
        await eventuallyAsync(async () => (await rowsAsync()).Count == 0, "the handled messages were never deleted");

        await receiving.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record NsbReceiveTxPing(Guid Run, string Name, bool FailFirst);

public class NsbReceiveTxFailure : Exception;

public class NsbReceiveTxPingHandler
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _handled = new();

    public static string[] HandledFor(Guid run) =>
        _handled.TryGetValue(run, out var handled) ? handled.ToArray() : [];

    public static void Handle(NsbReceiveTxPing ping)
    {
        var handled = _handled.GetOrAdd(ping.Run, _ => new ConcurrentQueue<string>());
        var first = !handled.Contains(ping.Name);
        handled.Enqueue(ping.Name);

        if (ping.FailFirst && first) throw new NsbReceiveTxFailure();
    }
}

// Holds every delivery until the test releases it, then settles it the way the test says
internal class GatedReceiver5815 : IReceiver
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
