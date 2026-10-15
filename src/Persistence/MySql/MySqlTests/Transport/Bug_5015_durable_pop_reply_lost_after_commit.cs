using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
/// The MySQL leg of https://github.com/JasperFx/wolverine/issues/5015. The durable pop already moves and reads
/// inside one transaction, so a lost reply to the read rolls back. A lost reply to the COMMIT is the remaining
/// window: the move may have committed, leaving the envelopes owned by this live node, which nothing recovers.
/// A TCP proxy between the listener and MySQL forwards a request and withholds the reply.
/// </summary>
[Collection("mysql")]
public class Bug_5015_durable_pop_reply_lost_after_commit : IAsyncLifetime
{
    private const string DatabaseName = "wolverine_gh5015";
    private const int NodeNumber = 21;

    // MySqlConnector commits with a COM_QUERY packet: a 4 byte header, the 0x03 command, two query attribute
    // bytes, then the text "commit"
    private static readonly Func<byte[], bool> IsCommit = chunk =>
        chunk.Length == 13 && chunk[4] == 0x03 &&
        Encoding.ASCII.GetString(chunk, 7, 6).Equals("commit", StringComparison.OrdinalIgnoreCase);

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private MySqlQueue _queue = null!;
    private ReplyWithholdingProxy _proxy = null!;
    private MySqlDataSource _proxiedDataSource = null!;
    private MySqlQueueListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS {DatabaseName}; CREATE DATABASE {DatabaseName};";
            await cmd.ExecuteNonQueryAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema: DatabaseName,
                    transportSchema: DatabaseName);

                // Declare the queue without listening to it: the test drives its own listener, and a host
                // listener could pop the message first
                opts.PublishMessage<Bug5015Message>().ToMySqlQueue("gh5015");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _queue = _runtime.Options.Transports.GetOrCreate<MySqlTransport>().Queues["gh5015"];

        var upstream = new MySqlConnectionStringBuilder(Servers.MySqlConnectionString);
        _proxy = ReplyWithholdingProxy.Start(upstream.Server, (int)upstream.Port);

        _proxiedDataSource = new MySqlDataSource(new MySqlConnectionStringBuilder(Servers.MySqlConnectionString)
        {
            Server = "127.0.0.1",
            Port = (uint)_proxy.Port,
            SslMode = MySqlSslMode.None,
            AllowPublicKeyRetrieval = true,
            Pooling = false,
            ConnectionTimeout = 2,
            DefaultCommandTimeout = 2,
            CancellationTimeout = 1
        }.ConnectionString);

        var receiver = new DurableReceiver(_queue, _runtime, Substitute.For<IHandlerPipeline>());
        _listener = new MySqlQueueListener(_queue, _runtime, receiver, _proxiedDataSource, null);
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

        _proxy.WithholdReplyTo("SELECT id, body FROM temp_pop");

        await Should.ThrowAsync<Exception>(popAsync);
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

        var popped = await popAsync();
        popped.Single().Id.ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task a_lost_reply_to_the_commit_releases_the_moved_envelopes_to_recovery()
    {
        var envelope = await sendOneAsync();

        _proxy.WithholdReplyTo(IsCommit);

        await Should.ThrowAsync<Exception>(popAsync);
        _proxy.Tripped.ShouldBeTrue();

        (await _queue.CountAsync()).ShouldBe(0);
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
        _listener.HasUnconfirmedPop.ShouldBeFalse();
    }

    [Fact]
    public async Task an_unconfirmed_pop_that_cannot_be_released_yet_is_released_later()
    {
        var envelope = await sendOneAsync();

        _proxy.WithholdReplyTo(IsCommit, unreachableAfterwards: true);

        await Should.ThrowAsync<Exception>(popAsync);
        _listener.HasUnconfirmedPop.ShouldBeTrue();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(NodeNumber);

        _proxy.Restore();
        await _listener.ReleaseUnconfirmedPopAsync(CancellationToken.None);

        _listener.HasUnconfirmedPop.ShouldBeFalse();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
    }
}

public record Bug5015Message;
