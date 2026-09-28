using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4645.
///
/// <para>
/// Everything handed to <see cref="WolverineRuntime.EnqueueDirectlyAsync"/> was read out of the inbox:
/// the scheduled poller promotes a due row to <c>Incoming</c>, takes ownership of it, commits, and
/// hands the batch over. When the destination has a listener circuit on this node, the receiver settles
/// that row as part of handling the message. When it does not — this node is publishing to a queue
/// another node listens to — the envelope was forwarded through a sending agent and the row was simply
/// abandoned: still <c>Incoming</c>, still owned by a live node, and nothing left to retire it.
/// </para>
///
/// <para>
/// On the database-backed queue transports that abandoned row destroys the message. A scheduled
/// envelope parks in the inbox under its eventual <em>destination</em>
/// (<c>IEnvelopeTransaction.PersistAsync</c> sends <c>Scheduled</c> to <c>PersistIncomingAsync</c>), and
/// the anti-duplicate probe each queue listener runs before every pop (GH-4316) deletes any queue row
/// whose id is already in the inbox at that queue's address — with no status filter. So the owning node
/// deletes the row this node just wrote, on its very next poll, and the message is never handled, with
/// nothing logged and nothing dead lettered. Marking the row <c>Handled</c> rather than deleting it does
/// not help: a retained Handled row inside <c>KeepAfterMessageHandling</c> matches the same probe.
/// </para>
///
/// <para>
/// On every other transport the same orphan is a quieter bug of its own — once the forwarding node dies
/// and its rows are released back to <c>AnyNode</c>, recovery re-enqueues a message that was already
/// delivered.
/// </para>
/// </summary>
public class Bug_4645_enqueue_directly_strands_the_inbox_row : IAsyncLifetime
{
    // A registered, sendable transport that this node has no listener for -- the same shape as a
    // PostgreSQL queue whose exclusive listener agent lives on the other node.
    private static readonly Uri TheRemoteDestination = "stub://orders1".ToUri();

    private static readonly Uri TheLocalDestination = "local://items".ToUri();

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<ForwardedMessageHandler>())
            .StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static (Envelope, IMessageInbox) recoveredEnvelope(Uri destination)
    {
        var inbox = Substitute.For<IMessageInbox>();
        var store = Substitute.For<IMessageStore>();
        store.Inbox.Returns(inbox);

        var envelope = new Envelope(new ForwardedMessage("due"))
        {
            Destination = destination,

            // The scheduled poller stamps the owning store on every envelope it recovers
            Store = store
        };

        return (envelope, inbox);
    }

    [Fact]
    public async Task delete_the_inbox_row_after_forwarding_through_a_sender()
    {
        var (envelope, inbox) = recoveredEnvelope(TheRemoteDestination);

        await _host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        await inbox.Received().DeleteIncomingEnvelopeAsync(envelope);
    }

    [Fact]
    public async Task the_row_is_deleted_rather_than_marked_handled()
    {
        // Load-bearing, not a style preference. The GH-4316 probe has no status filter, so a Handled row
        // inside the KeepAfterMessageHandling window suppresses the queue row just as an Incoming one does.
        var (envelope, inbox) = recoveredEnvelope(TheRemoteDestination);

        await _host.GetRuntime().EnqueueDirectlyAsync([envelope]);

        await inbox.DidNotReceive().MarkIncomingEnvelopeAsHandledAsync(envelope);
    }

    [Fact]
    public async Task leave_the_row_alone_when_a_listener_circuit_takes_the_envelope()
    {
        // The negative control. A destination this node DOES listen to is settled by the receiver once the
        // message is actually handled -- retiring it here would retire it before it had been.
        var (envelope, inbox) = recoveredEnvelope(TheLocalDestination);

        await _host.TrackActivity()
            .ExecuteAndWaitAsync(_ => _host.GetRuntime().EnqueueDirectlyAsync([envelope]).AsTask());

        await inbox.DidNotReceive().DeleteIncomingEnvelopeAsync(envelope);
    }

    [Fact]
    public async Task a_failed_delete_does_not_strand_the_rest_of_the_batch()
    {
        // The envelope is already on its way to the destination by the time the delete runs, so throwing
        // here would lose the rest of the batch AND have the poller forward this one again next pass.
        var (failing, failingInbox) = recoveredEnvelope(TheRemoteDestination);
        failingInbox.DeleteIncomingEnvelopeAsync(failing).Throws(new TimeoutException("database is busy"));

        var (second, secondInbox) = recoveredEnvelope(TheRemoteDestination);

        await Should.NotThrowAsync(() =>
            _host.GetRuntime().EnqueueDirectlyAsync([failing, second]).AsTask());

        await secondInbox.Received().DeleteIncomingEnvelopeAsync(second);
    }
}

public record ForwardedMessage(string Name);

public class ForwardedMessageHandler
{
    public void Handle(ForwardedMessage message)
    {
    }
}
