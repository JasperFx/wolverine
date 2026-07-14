using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/3413.
///
/// <para>
/// The scheduled message poller in every RDBMS message store reads a batch of due
/// envelopes out of the incoming table, reassigns them to this node, commits, and then
/// hands the batch to <see cref="WolverineRuntime.EnqueueDirectlyAsync"/>. If any envelope
/// in that batch names a destination whose transport is not registered on this node —
/// a row left behind by an older deployment, or by another test suite sharing the schema —
/// <c>GetOrBuildSendingAgent</c> threw <see cref="UnknownTransportException"/>.
/// </para>
///
/// <para>
/// That throw came *after* the reassigning commit, so the rest of the batch was lost, and
/// the poller rediscovered and rethrew on the offending row on every subsequent run. Such a
/// destination can never become sendable on this node, so the envelope is dead lettered
/// instead and the rest of the batch goes through.
/// </para>
/// </summary>
public class Bug_3413_unknown_destination_on_scheduled_recovery : IAsyncLifetime
{
    private static readonly Uri TheUnknownDestination = "rabbitmq://queue/items".ToUri();
    private static readonly Uri TheLocalDestination = "local://items".ToUri();

    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // Note that there is no Rabbit MQ transport registered in this application
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<OrphanedMessageHandler>())
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private (Envelope, IMessageInbox) orphanedEnvelope()
    {
        var inbox = Substitute.For<IMessageInbox>();
        var store = Substitute.For<IMessageStore>();
        store.Inbox.Returns(inbox);

        var envelope = new Envelope(new OrphanedMessage("stale"))
        {
            Destination = TheUnknownDestination,

            // The scheduled poller stamps the owning store on every envelope it recovers
            Store = store
        };

        return (envelope, inbox);
    }

    [Fact]
    public async Task dead_letter_an_envelope_whose_transport_cannot_be_resolved()
    {
        var (envelope, inbox) = orphanedEnvelope();

        await _host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        await inbox.Received().MoveToDeadLetterStorageAsync(envelope, Arg.Any<UnknownTransportException>());
    }

    [Fact]
    public async Task still_deliver_the_rest_of_the_batch()
    {
        var (orphan, _) = orphanedEnvelope();

        var good = new Envelope(new OrphanedMessage("deliver me"))
        {
            Destination = TheLocalDestination
        };

        var session = await _host.ExecuteAndWaitAsync(
            () => _host.GetRuntime().EnqueueDirectlyAsync([orphan, good]).AsTask());

        session.Received.SingleMessage<OrphanedMessage>()
            .Name.ShouldBe("deliver me");
    }
}

public record OrphanedMessage(string Name);

public class OrphanedMessageHandler
{
    public void Handle(OrphanedMessage message)
    {
    }
}
