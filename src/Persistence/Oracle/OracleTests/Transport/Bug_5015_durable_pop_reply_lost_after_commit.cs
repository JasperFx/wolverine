using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace OracleTests.Transport;

/// <summary>
/// The Oracle leg of https://github.com/JasperFx/wolverine/issues/5015. The durable pop reads every row before
/// it moves anything, inside one transaction, so the only window left is a lost reply to the COMMIT: the move
/// may have committed, leaving the envelopes owned by this live node, which nothing recovers. A TCP proxy
/// between the listener and Oracle forwards the commit and then cuts the connection without its reply.
/// </summary>
[Collection("oracle")]
public class Bug_5015_durable_pop_reply_lost_after_commit : IAsyncLifetime
{
    // A commit is a short TNS data packet (type 0x06) whose TTC payload, after the 10 byte header, calls function
    // 0x0E (OCOMMIT) with the 0x03 function-call marker, sometimes behind a piggybacked cursor close
    private static readonly byte[] CommitCall = [0x03, 0x0E];

    private static readonly Func<byte[], bool> IsCommit = chunk =>
        chunk.Length is > 10 and < 64 && chunk[4] == 0x06 && chunk.AsSpan(10).IndexOf(CommitCall) >= 0;

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private OracleQueue _queue = null!;
    private ReplyWithholdingProxy _proxy = null!;
    private OracleDataSource _proxiedDataSource = null!;
    private OracleQueueListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            foreach (var table in new[] { "WOLVERINE_QUEUE_GH5015", "WOLVERINE_QUEUE_GH5015_SCHEDULED" })
            {
                try
                {
                    await using var cmd = conn.CreateCommand($"DROP TABLE WOLVERINE.{table} CASCADE CONSTRAINTS");
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (OracleException)
                {
                    // the table does not exist
                }
            }
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport();
                opts.Durability.Mode = DurabilityMode.Solo;

                // Declare the queue without listening to it: the test drives its own listener, and a host
                // listener could pop the message first
                opts.PublishMessage<Bug5015Message>().ToOracleQueue("gh5015");
            }).StartAsync();

        _runtime = _host.GetRuntime();
        _runtime.DurabilitySettings.AssignedNodeNumber.ShouldNotBe(0);
        _queue = _runtime.Options.Transports.GetOrCreate<OracleTransport>().Queues["gh5015"];

        var upstream = new OracleConnectionStringBuilder(Servers.OracleConnectionString);
        var hostAndRest = upstream.DataSource.Split('/', 2);
        var hostAndPort = hostAndRest[0].Split(':');
        _proxy = ReplyWithholdingProxy.Start(hostAndPort[0], hostAndPort.Length > 1 ? int.Parse(hostAndPort[1]) : 1521);

        _proxiedDataSource = new OracleDataSource(new OracleConnectionStringBuilder(Servers.OracleConnectionString)
        {
            DataSource = $"127.0.0.1:{_proxy.Port}/{hostAndRest[1]}",
            Pooling = false,
            ConnectionTimeout = 2
        }.ConnectionString);

        var receiver = new DurableReceiver(_queue, _runtime, Substitute.For<IHandlerPipeline>());
        _listener = new OracleQueueListener(_queue, _runtime, receiver, _proxiedDataSource, null);
    }

    public async ValueTask DisposeAsync()
    {
        await _listener.DisposeAsync();
        _proxiedDataSource.Dispose();
        _proxy.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<IReadOnlyList<Envelope>> popAsync()
    {
        return _listener.TryPopDurablyAsync(10, _runtime.DurabilitySettings, NullLogger.Instance,
            CancellationToken.None);
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
    public async Task a_lost_reply_to_the_commit_releases_the_moved_envelopes_to_recovery()
    {
        var envelope = await sendOneAsync();

        _proxy.WithholdReplyTo(IsCommit, severAfter: 500.Milliseconds());

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

        _proxy.WithholdReplyTo(IsCommit, unreachableAfterwards: true, severAfter: 500.Milliseconds());

        await Should.ThrowAsync<Exception>(popAsync);
        _listener.HasUnconfirmedPop.ShouldBeTrue();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(_runtime.DurabilitySettings.AssignedNodeNumber);

        _proxy.Restore();
        await _listener.ReleaseUnconfirmedPopAsync(CancellationToken.None);

        _listener.HasUnconfirmedPop.ShouldBeFalse();
        (await inboxOwnerOfAsync(envelope)).ShouldBe(0);
    }
}

public record Bug5015Message;
