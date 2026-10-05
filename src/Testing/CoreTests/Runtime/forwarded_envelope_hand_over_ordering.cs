using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
///     GH-4824. A scheduled envelope that comes due on a node that does not listen to its destination is
///     forwarded through a sending agent, and its inbox row has to be retired. GH-4645 added that delete
///     but left the send first, which leaves two losses:
///
///     <para>the owning node's anti-duplicate probe (GH-4316) deletes the new queue row because the inbox
///     row is still there, and the message is never handled, logged or dead lettered; and a failed first
///     send leaves nothing but the agent's in-memory retry block holding the message, because
///     <c>EnqueueOutgoingAsync</c> stores no outgoing row.</para>
///
///     <para>So for a durable agent the order is store, delete, send. The order is the entire fix, which
///     is why it is what gets asserted.</para>
/// </summary>
public class forwarded_envelope_hand_over_ordering : IAsyncLifetime
{
    private readonly List<string> theCalls = [];
    private IHost _host = null!;
    private Envelope theEnvelope = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .StartAsync();

        // The envelope carries its own store, so retireForwardedInboxRowAsync never reaches the runtime's
        // real (null) storage and the delete is observable.
        var inbox = Substitute.For<IMessageInbox>();
        inbox.DeleteIncomingEnvelopeAsync(Arg.Any<Envelope>())
            .Returns(_ =>
            {
                theCalls.Add("delete");
                return Task.CompletedTask;
            });

        var store = Substitute.For<IMessageStore>();
        store.Inbox.Returns(inbox);

        theEnvelope = new Envelope(new ForwardedHandOverMessage())
        {
            Id = Guid.NewGuid(),
            Destination = new Uri("stub://outbound"),
            Store = store
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_durable_agent_stores_the_outgoing_row_before_the_inbox_row_is_retired()
    {
        var sender = new RecordingSendingAgent(theCalls, true);
        var handedOver = new HashSet<Envelope>();

        await _host.GetRuntime().HandOverAsync(sender, theEnvelope, handedOver);

        // The whole point: the outbox row exists before the inbox row goes away, and the queue row the
        // probe could match only appears once the inbox row is already gone.
        theCalls.ShouldBe(["store", "delete", "send"]);

        // Marked handed over as soon as the outbox row exists. A release back to AnyNode after that point
        // would let recovery deliver the message twice.
        handedOver.ShouldContain(theEnvelope);
    }

    [Fact]
    public async Task an_agent_with_no_outbox_keeps_the_original_order()
    {
        var sender = new RecordingSendingAgent(theCalls, false);
        var handedOver = new HashSet<Envelope>();

        await _host.GetRuntime().HandOverAsync(sender, theEnvelope, handedOver);

        // Nothing to move the message INTO, so reordering here would only widen the window in which no
        // table holds it at all.
        theCalls.ShouldBe(["send", "delete"]);
        handedOver.ShouldContain(theEnvelope);
    }

    [Fact]
    public async Task the_inbox_row_is_retired_at_the_address_it_was_parked_under()
    {
        // The partition-slot forward rewrites Destination before handing the envelope over, and the delete
        // matches on id AND received_at -- so a delete by the live destination would miss the row and
        // strand it, which is the GH-4645 loss all over again.
        var parkedAt = new Uri("local://companion-queue");
        var sender = new RecordingSendingAgent(theCalls, true);

        await _host.GetRuntime().HandOverAsync(sender, theEnvelope, new HashSet<Envelope>(), parkedAt);

        await theEnvelope.Store!.Inbox.Received()
            .DeleteIncomingEnvelopeAsync(Arg.Is<Envelope>(x => x.Id == theEnvelope.Id && x.Destination == parkedAt));
    }

    [Fact]
    public void the_durable_sending_agent_is_the_one_agent_that_answers_true()
    {
        // The ordering asserted above only takes effect for an agent that really overrides this. Every
        // test here drives a FAKE, so a DurableSendingAgent that silently inherited the base class's
        // `false` would leave this file green while production kept the losing order.
        typeof(DurableSendingAgent)
            .GetMethod(nameof(ISendingAgent.TryStoreOutgoingAsync))!
            .DeclaringType.ShouldBe(typeof(DurableSendingAgent));

        // ...and the buffered agent deliberately does not: it has no outbox to store into.
        typeof(BufferedSendingAgent)
            .GetMethod(nameof(ISendingAgent.TryStoreOutgoingAsync))!
            .DeclaringType.ShouldBe(typeof(SendingAgent));
    }

    [Fact]
    public async Task an_existing_sending_agent_that_knows_nothing_about_this_answers_false()
    {
        // TryStoreOutgoingAsync is a DEFAULT interface member precisely so an ISendingAgent outside this
        // repository keeps compiling and keeps today's ordering. Asserted against a type that does not
        // implement it, because that is the whole contract.
        ISendingAgent agent = new UnawareSendingAgent();

        (await agent.TryStoreOutgoingAsync(theEnvelope)).ShouldBeFalse();
    }
}

internal class RecordingSendingAgent : ISendingAgent
{
    private readonly List<string> _calls;
    private readonly bool _durable;

    public RecordingSendingAgent(List<string> calls, bool durable)
    {
        _calls = calls;
        _durable = durable;
    }

    public Uri Destination { get; } = new("stub://outbound");
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => _durable;
    public bool SupportsNativeScheduledSend => false;
    public Endpoint Endpoint => throw new NotSupportedException();
    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        _calls.Add("send");
        return ValueTask.CompletedTask;
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        _calls.Add("store-and-forward");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryStoreOutgoingAsync(Envelope envelope)
    {
        if (!_durable)
        {
            return new ValueTask<bool>(false);
        }

        _calls.Add("store");
        return new ValueTask<bool>(true);
    }
}

/// <summary>
///     Deliberately does NOT implement <see cref="ISendingAgent.TryStoreOutgoingAsync" />.
/// </summary>
internal class UnawareSendingAgent : ISendingAgent
{
    public Uri Destination { get; } = new("stub://unaware");
    public Uri? ReplyUri { get; set; }
    public bool Latched => false;
    public bool IsDurable => false;
    public bool SupportsNativeScheduledSend => false;
    public Endpoint Endpoint => throw new NotSupportedException();
    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask StoreAndForwardAsync(Envelope envelope) => ValueTask.CompletedTask;
}

public record ForwardedHandOverMessage;
