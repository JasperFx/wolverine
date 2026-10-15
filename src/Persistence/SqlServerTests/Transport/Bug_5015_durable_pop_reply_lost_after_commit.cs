using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
/// The SQL Server twin of https://github.com/JasperFx/wolverine/issues/5015. The durable pop ran its DELETE,
/// inbox INSERT and SELECT as separate auto-committed statements, so the move was durable before the client
/// read the reply: a read failure left the envelopes in the inbox owned by this live node, which nothing
/// recovers. A TCP proxy between the listener and SQL Server forwards a request and withholds the reply.
/// </summary>
public class Bug_5015_durable_pop_reply_lost_after_commit : IAsyncLifetime
{
    private const string SchemaName = "gh5015";
    private const int NodeNumber = 21;

    // TDS: a Transaction Manager request is packet type 0x0E. After the 8 byte packet header and the 22 byte
    // ALL_HEADERS block comes the request type, and 7 is TM_COMMIT_XACT. Under MARS every packet is wrapped in a
    // 16 byte SMP header that starts with 0x53.
    private static readonly Func<byte[], bool> IsCommit = chunk =>
    {
        var offset = chunk.Length > 0 && chunk[0] == 0x53 ? 16 : 0;
        return chunk.Length > offset + 31 && chunk[offset] == 0x0E && chunk[offset + 30] == 7 &&
               chunk[offset + 31] == 0;
    };

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private SqlServerQueue _queue = null!;
    private ReplyWithholdingProxy _proxy = null!;
    private SqlServerQueueListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(SchemaName);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName)
                    .AutoProvision();

                // Declare the queue without listening to it: the test drives its own listener, and a host
                // listener could pop the message first
                opts.PublishMessage<Bug5015Message>().ToSqlServerQueue("gh5015");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _queue = _runtime.Options.Transports.OfType<SqlServerTransport>().Single().Queues["gh5015"];

        var upstream = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);
        var dataSource = upstream.DataSource.Split(',');
        _proxy = ReplyWithholdingProxy.Start(dataSource[0], dataSource.Length > 1 ? int.Parse(dataSource[1]) : 1433);

        var proxied = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString)
        {
            DataSource = $"127.0.0.1,{_proxy.Port}",
            Pooling = false,
            ConnectTimeout = 2,
            CommandTimeout = 2,
            Encrypt = SqlConnectionEncryptOption.Optional
        }.ConnectionString;

        var receiver = new DurableReceiver(_queue, _runtime, Substitute.For<IHandlerPipeline>());
        _listener = new SqlServerQueueListener(_queue, _runtime, receiver, proxied, null);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
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
        await _queue.SendAsync(envelope, CancellationToken.None);
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

        // TDS sends SQL text as UTF-16
        _proxy.WithholdReplyTo("DECLARE @popped", Encoding.Unicode);

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
        _listener.DurablePop.HasUnconfirmedPop.ShouldBeFalse();
    }

    [Fact]
    public async Task an_unconfirmed_pop_that_cannot_be_released_yet_is_released_later()
    {
        var envelope = await sendOneAsync();

        _proxy.WithholdReplyTo(IsCommit, unreachableAfterwards: true);

        await Should.ThrowAsync<Exception>(popAsync);
        _listener.DurablePop.HasUnconfirmedPop.ShouldBeTrue();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(NodeNumber);

        _proxy.Restore();
        await _listener.DurablePop.ReleaseUnconfirmedPopAsync(CancellationToken.None);

        _listener.DurablePop.HasUnconfirmedPop.ShouldBeFalse();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
    }
}

public record Bug5015Message;
