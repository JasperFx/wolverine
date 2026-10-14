using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// Follow-up to https://github.com/JasperFx/wolverine/issues/5015 on top of Bug5015DurablePopResponseLoss.
/// When the COMMIT reply of a durable pop is lost, the batch is released to inbox recovery on a fresh
/// connection. If the database is still unreachable for that release too, the batch must not be dropped: the
/// listener keeps it and releases it before its next poll.
/// </summary>
[Collection("Postgresql")]
public class Bug_5015_release_retried_after_lost_commit : IAsyncLifetime
{
    private const string SchemaName = "gh5015";
    private const int NodeNumber = 21;

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private PostgresqlQueue _queue = null!;
    private ReplyWithholdingProxy _proxy = null!;
    private NpgsqlDataSource _proxiedDataSource = null!;
    private PostgresqlQueueListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, SchemaName,
                    transportSchema: SchemaName);

                // Declare the queue without listening to it: the test drives its own listener, and a host
                // listener could pop the message first
                opts.PublishMessage<Bug5015Message>().ToPostgresqlQueue("gh5015");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _queue = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["gh5015"];

        var upstream = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);
        _proxy = ReplyWithholdingProxy.Start(upstream.Host!, upstream.Port);

        _proxiedDataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Host = "127.0.0.1",
            Port = _proxy.Port,
            SslMode = SslMode.Disable,
            Pooling = false,
            Timeout = 2,
            CommandTimeout = 2,
            CancellationTimeout = 500
        }.ConnectionString);

        var receiver = new DurableReceiver(_queue, _runtime, Substitute.For<IHandlerPipeline>());
        _listener = new PostgresqlQueueListener(_queue, _runtime, receiver, _proxiedDataSource, null);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        await _proxiedDataSource.DisposeAsync();
        _proxy.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<IReadOnlyList<Envelope>> popAsync()
    {
        return _listener.TryPopDurablyAsync(10, new DurabilitySettings { AssignedNodeNumber = NodeNumber },
            NullLogger.Instance, CancellationToken.None);
    }

    private async Task<Envelope> sendOneAsync()
    {
        var envelope = ObjectMother.Envelope();
        await _queue.SendAsync(envelope);
        (await _queue.CountAsync()).ShouldBe(1);
        return envelope;
    }

    private async Task<int?> inboxOwnerOfAsync(Envelope envelope)
    {
        var incoming = await _runtime.Storage.Admin.AllIncomingAsync();
        return incoming.SingleOrDefault(x => x.Id == envelope.Id)?.OwnerId;
    }

    [Fact]
    public async Task an_unconfirmed_pop_that_cannot_be_released_yet_is_released_later()
    {
        var envelope = await sendOneAsync();

        // The commit's reply is lost and the database stays unreachable for the release that follows
        _proxy.WithholdReplyTo("COMMIT\0", unreachableAfterwards: true);

        await Should.ThrowAsync<NpgsqlException>(popAsync);
        _listener.HasUnconfirmedPop.ShouldBeTrue();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(NodeNumber);

        // The database comes back; the receive loop releases the unconfirmed pop before its next poll
        _proxy.Restore();
        await _listener.ReleaseUnconfirmedPopAsync(NullLogger.Instance, CancellationToken.None);

        _listener.HasUnconfirmedPop.ShouldBeFalse();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
    }
}

public record Bug5015Message;
