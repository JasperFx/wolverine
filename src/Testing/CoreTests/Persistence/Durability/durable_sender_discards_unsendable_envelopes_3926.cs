using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-3926. A transport that has decided an envelope can never be sent — SQS answers an oversized
/// message with a permanent SenderFault — reports it through MarkSerializationFailureAsync. That used
/// to only log, so on a durable endpoint the row stayed in the outgoing table and the durability agent
/// re-read and re-sent it on every recovery sweep: the same endless retry the call exists to break.
/// </summary>
public class durable_sender_discards_unsendable_envelopes_3926
{
    private readonly IMessageOutbox _outbox = Substitute.For<IMessageOutbox>();
    private readonly DurableSendingAgent _agent;
    private readonly Uri _destination = new("stub://one");

    public durable_sender_discards_unsendable_envelopes_3926()
    {
        var sender = Substitute.For<ISender>();
        sender.Destination.Returns(_destination);

        _agent = new DurableSendingAgent(sender, new DurabilitySettings(), NullLogger.Instance,
            Substitute.For<IMessageTracker>(), _outbox, new UnsendableEndpoint(_destination));
    }

    private class UnsendableEndpoint : Endpoint
    {
        public UnsendableEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            throw new NotSupportedException();
        }

        protected override bool supportsMode(EndpointMode mode) => true;
    }

    private static Envelope envelope()
    {
        return new Envelope { Id = Guid.NewGuid(), Data = [1, 2, 3], MessageType = "too.big" };
    }

    [Fact]
    public async Task deletes_the_outgoing_rows_so_recovery_cannot_resend_them()
    {
        var one = envelope();
        var two = envelope();

        await ((ISenderCallback)_agent).MarkSerializationFailureAsync(
            new OutgoingMessageBatch(_destination, [one, two]));

        await _agent.DisposeAsync();

        await _outbox.Received().DeleteOutgoingAsync(Arg.Is<Envelope[]>(x =>
            x.Length == 2 && x.Contains(one) && x.Contains(two)));
    }

    [Fact]
    public async Task does_not_re_queue_them_for_another_attempt()
    {
        var unsendable = envelope();

        await ((ISenderCallback)_agent).MarkSerializationFailureAsync(
            new OutgoingMessageBatch(_destination, [unsendable]));

        await _agent.DisposeAsync();

        // Storing it again is what a retry would look like from the outbox's side
        await _outbox.DidNotReceive().StoreOutgoingAsync(Arg.Any<Envelope>(), Arg.Any<int>());
    }
}
