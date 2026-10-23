using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using CoreTests.Runtime;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-5055. When a transport cannot map an outgoing envelope to its broker message, it reports the envelope through
/// ISenderCallback.MarkSerializationFailureAsync, since mapping it again can never succeed. The sending agent then
/// moves it to dead letter storage, in every durability mode, rather than only logging it. A durable agent deletes
/// its outbox row once the dead letter is written
/// </summary>
public class unmappable_outgoing_envelopes_are_dead_lettered_5055 : IAsyncDisposable
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly IMessageOutbox theOutbox = Substitute.For<IMessageOutbox>();
    private readonly UnmappableProtocol theProtocol = new();
    private readonly TcpEndpoint theEndpoint = new(2346);
    private readonly BatchedSender theSender;
    private readonly List<IAsyncDisposable> _disposables = new();

    public unmappable_outgoing_envelopes_are_dead_lettered_5055()
    {
        theSender = new BatchedSender(theEndpoint, theProtocol, CancellationToken.None, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private DurableSendingAgent durableAgent()
    {
        var agent = new DurableSendingAgent(theSender, theRuntime.Options.Durability, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), theOutbox, theEndpoint, theRuntime, null);
        _disposables.Add(agent);
        return agent;
    }

    private Envelope envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = theEndpoint.Uri;
        envelope.DeliverBy = null;
        return envelope;
    }

    // What the transport protocols do on a mapping failure, one envelope of three
    private Task sendBatchOfThreeAsync(ISenderCallback agent, out Envelope[] mapped, out Envelope unmappable)
    {
        var one = envelope();
        unmappable = envelope();
        var three = envelope();
        theProtocol.Unmappable.Add(unmappable.Id);
        mapped = [one, three];

        return theProtocol.SendBatchAsync(agent, new OutgoingMessageBatch(theEndpoint.Uri, [one, unmappable, three]));
    }

    [Fact]
    public async Task a_durable_agent_dead_letters_the_unmappable_envelope_and_deletes_only_its_rows_once_sent()
    {
        var agent = durableAgent();

        await sendBatchOfThreeAsync(agent, out var mapped, out var unmappable);
        await agent.DisposeAsync(); // flushes the background deletes

        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(unmappable,
            Arg.Is<Exception>(e => e is UnsendableEnvelopeException));
        foreach (var envelope in mapped)
        {
            await theRuntime.Storage.Inbox.DidNotReceive().MoveToDeadLetterStorageAsync(envelope, Arg.Any<Exception>());
        }

        theRuntime.MessageTracking.Received(1).MovedToErrorQueue(unmappable, Arg.Any<Exception>());

        // The two sent and the one dead lettered all leave the outbox, each exactly once
        await theOutbox.Received(1).DeleteOutgoingAsync(Arg.Is<Envelope[]>(x =>
            x.Length == 2 && x.Contains(mapped[0]) && x.Contains(mapped[1])));
        await theOutbox.Received(1).DeleteOutgoingAsync(Arg.Is<Envelope[]>(x =>
            x.Length == 1 && x[0] == unmappable));
    }

    [Fact]
    public async Task a_durable_agent_leaves_the_row_in_the_outbox_when_the_dead_letter_cannot_be_written()
    {
        var agent = durableAgent();
        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(Arg.Any<Envelope>(), Arg.Any<Exception>())
            .Returns(Task.FromException(new InvalidOperationException("database is down")));

        await sendBatchOfThreeAsync(agent, out _, out var unmappable);
        await agent.DisposeAsync();

        // Nothing is lost: a later recovery sweep tries it again
        await theOutbox.DidNotReceive().DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Contains(unmappable)));
    }

    [Fact]
    public async Task a_buffered_agent_dead_letters_the_unmappable_envelope()
    {
        var agent = new BufferedSendingAgent(NullLogger.Instance, Substitute.For<IMessageTracker>(), theSender,
            theRuntime.Options.Durability, theEndpoint, theRuntime, null);
        _disposables.Add(agent);

        await sendBatchOfThreeAsync(agent, out var mapped, out var unmappable);

        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(unmappable, Arg.Any<Exception>());
        await theRuntime.Storage.Inbox.DidNotReceive().MoveToDeadLetterStorageAsync(mapped[0], Arg.Any<Exception>());
    }

    [Fact]
    public async Task the_serverless_inline_send_dead_letters_an_unmappable_envelope_right_away()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        var agent = durableAgent();

        var unmappable = envelope();
        theProtocol.Unmappable.Add(unmappable.Id);

        await agent.StoreAndForwardAsync(unmappable);

        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(unmappable, Arg.Any<Exception>());
        await theOutbox.Received(1).DeleteOutgoingAsync(unmappable);
    }

    [Fact]
    public async Task the_serverless_inline_send_leaves_a_failed_send_in_the_outbox()
    {
        theRuntime.Options.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        var agent = durableAgent();

        var throttled = envelope();
        theProtocol.Rejected.Add(throttled.Id);

        await agent.StoreAndForwardAsync(throttled);

        await theRuntime.Storage.Inbox.DidNotReceiveWithAnyArgs().MoveToDeadLetterStorageAsync(default!, default);
        await theOutbox.DidNotReceive().DeleteOutgoingAsync(throttled);
    }

    /// <summary>
    /// Reports a batch the way the fixed Azure Service Bus, SNS and Kafka protocols do when their mapper throws for
    /// some of it: those as a serialization failure, and only what was really sent as successful
    /// </summary>
    private class UnmappableProtocol : ISenderProtocol
    {
        public HashSet<Guid> Unmappable { get; } = new();
        public HashSet<Guid> Rejected { get; } = new();

        public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
        {
            var unmappable = batch.Messages.Where(x => Unmappable.Contains(x.Id)).ToList();
            var rejected = batch.Messages.Where(x => Rejected.Contains(x.Id)).ToList();
            var sent = batch.Messages.Except(unmappable).Except(rejected).ToList();

            if (unmappable.Count > 0)
            {
                await callback.MarkSerializationFailureAsync(new OutgoingMessageBatch(batch.Destination, unmappable));
            }

            if (sent.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, sent));
            }

            if (rejected.Count > 0)
            {
                await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, rejected),
                    new InvalidOperationException("Throttled"));
            }
        }
    }
}
