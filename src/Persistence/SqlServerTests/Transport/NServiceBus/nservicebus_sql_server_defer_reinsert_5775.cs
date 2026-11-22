using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport.NServiceBus;
using Wolverine.Transports;

namespace SqlServerTests.Transport.NServiceBus;

// GH-5775. The NServiceBus SQL Server queue pops with a destructive DELETE ... OUTPUT, so once a message has
// been received the only way it gets back into the queue is DeferAsync's re-insert. A failed re-insert used
// to lose the message silently.
public class nservicebus_sql_server_defer_reinsert_5775 : IAsyncLifetime
{
    private readonly string _queueName = "nsb_defer_" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;
    private NServiceBusSqlServerQueue _queue = null!;
    private IWolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "nsbdefer");
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);

                // Bounds how long a deferral keeps retrying its re-insert
                opts.Durability.DrainTimeout = 5.Seconds();

                opts.PublishMessage<NsbDeferPing>().ToNServiceBusSqlServerQueue(_queueName);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        _queue = _runtime.Options.Transports.GetOrCreate<NServiceBusSqlServerTransport>().Queues[_queueName];
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string table => _queue.TableIdentifier.QualifiedName;

    private async Task<SqlConnection> openAsync()
    {
        var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<long> scalarAsync(string sql, Guid? id = null)
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand(sql, conn);
        if (id.HasValue) cmd.Parameters.AddWithValue("id", id.Value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = await openAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task sendAsync(string name)
    {
        var before = await scalarAsync($"select count(*) from {table}");
        await _host.MessageBus().SendAsync(new NsbDeferPing(Guid.NewGuid(), name));

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (await scalarAsync($"select count(*) from {table}") == before)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the message never reached the queue table");
            await Task.Delay(50);
        }
    }

    // Pops messages off the table exactly the way a listening endpoint does
    private async Task<(NServiceBusSqlServerQueueListener listener, Envelope[] popped)> popAsync(int expected)
    {
        var receiver = new CapturingReceiver5775();
        var listener = new NServiceBusSqlServerQueueListener(_queue, _runtime, receiver);
        await listener.StartAsync();

        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (receiver.Count < expected)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the listener never popped the message");
            await Task.Delay(50);
        }

        await listener.StopAsync();
        return (listener, receiver.Envelopes);
    }

    private Task<long> countAsync(Guid id) => scalarAsync($"select count(*) from {table} where Id = @id", id);

    [Fact]
    public async Task a_reinsert_that_fails_for_a_while_is_retried_and_the_message_is_not_lost()
    {
        await sendAsync("one");
        var (listener, popped) = await popAsync(1);
        var envelope = popped.Single();
        (await countAsync(envelope.Id)).ShouldBe(0);

        // The table is unavailable when the message is deferred
        var hidden = _queue.TableIdentifier.Name + "_hidden";
        await executeAsync($"exec sp_rename '{table}', '{hidden}'");

        var defer = listener.DeferAsync(envelope).AsTask();

        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        await executeAsync($"exec sp_rename '{_queue.TableIdentifier.Schema}.{hidden}', '{_queue.TableIdentifier.Name}'");

        await defer;

        (await countAsync(envelope.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task a_reinsert_that_never_succeeds_surfaces_an_error_naming_the_message()
    {
        await sendAsync("one");
        var (listener, popped) = await popAsync(1);
        var envelope = popped.Single();

        await executeAsync($"drop table {table}");

        var ex = await Should.ThrowAsync<DatabaseQueueReinsertFailedException>(
            async () => await listener.DeferAsync(envelope));

        ex.EnvelopeId.ShouldBe(envelope.Id);
        ex.Attempts.ShouldBeGreaterThan(1);
        ex.Message.ShouldContain(envelope.Id.ToString());
    }

    [Fact]
    public async Task retrying_a_reinsert_that_already_committed_does_not_add_a_second_copy()
    {
        await sendAsync("one");
        var (listener, popped) = await popAsync(1);
        var envelope = popped.Single();

        await listener.DeferAsync(envelope);

        // A retry of the same deferral, as after a failure whose commit outcome was unknown
        await listener.DeferAsync(envelope);

        (await countAsync(envelope.Id)).ShouldBe(1);
    }
}

public record NsbDeferPing(Guid Id, string Name);

internal class CapturingReceiver5775 : IReceiver
{
    private readonly List<Envelope> _envelopes = new();

    public int Count
    {
        get
        {
            lock (_envelopes) return _envelopes.Count;
        }
    }

    public Envelope[] Envelopes
    {
        get
        {
            lock (_envelopes) return _envelopes.ToArray();
        }
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope[] messages)
    {
        lock (_envelopes) _envelopes.AddRange(messages);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope) => ReceivedAsync(listener, [envelope]);

    public ValueTask DrainAsync() => ValueTask.CompletedTask;

    public IHandlerPipeline Pipeline => null!;

    public void Dispose()
    {
    }
}
