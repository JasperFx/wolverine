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
/// Reproduction for https://github.com/JasperFx/wolverine/issues/5015, reported by @dbelinsky-accureference.
///
/// <para>
/// The durable pop moved a batch from the queue table into the inbox (status Incoming, owner_id = this
/// node) in one implicit transaction and only then read the moved bodies back. If the client failed while
/// reading that reply -- the server had already committed -- the receive loop logged the error and polled
/// again. The moved rows were then owned by a node that is alive and in no listener's memory: recovery only
/// loads owner_id = 0 and orphan release only touches dead nodes, so nothing ran them until a restart.
/// </para>
///
/// <para>
/// A TCP proxy between the listener and PostgreSQL forwards a request to the server and withholds the reply,
/// so the server executes and the client times out reading -- the reporter's disk stall, made deterministic.
/// </para>
/// </summary>
[Collection("Postgresql")]
public class Bug_5015_durable_pop_reply_lost_after_commit : IAsyncLifetime
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
    public async Task a_lost_reply_to_the_pop_rolls_the_move_back_to_the_queue()
    {
        var envelope = await sendOneAsync();

        _proxy.WithholdReplyTo("WITH popped AS");

        // This is what BackgroundReceiveLoop catches, logs, and polls past
        await Should.ThrowAsync<NpgsqlException>(popAsync);
        _proxy.Tripped.ShouldBeTrue();

        // The server rolls the move back once it sees the client's connection go away
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (await _queue.CountAsync() == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        (await _queue.CountAsync()).ShouldBe(1,
            $"The popped envelope never went back to the queue; inbox owner_id is {await inboxOwnerOfAsync(envelope)}");
        (await inboxOwnerOfAsync(envelope)).ShouldBeNull();

        // And the next poll picks it up
        var popped = await popAsync();
        popped.Single().Id.ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task a_lost_reply_to_the_commit_releases_the_moved_envelopes_to_recovery()
    {
        var envelope = await sendOneAsync();

        // Npgsql sends COMMIT as a simple query, null-terminated. The BEGIN in front of the pop says
        // "READ COMMITTED", which this does not match.
        _proxy.WithholdReplyTo("COMMIT\0");

        await Should.ThrowAsync<NpgsqlException>(popAsync);
        _proxy.Tripped.ShouldBeTrue();

        // The commit did land on the server. The move is not stranded on this live node, it is released
        // to the recovery sweep.
        (await _queue.CountAsync()).ShouldBe(0);
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
        _listener.HasUnconfirmedPop.ShouldBeFalse();
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
        await _listener.ReleaseUnconfirmedPopAsync(CancellationToken.None);

        _listener.HasUnconfirmedPop.ShouldBeFalse();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
    }
}

public record Bug5015Message;
