using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5047. A transport protocol that reports an envelope as a serialization failure, like SQS with a message over
/// its size limit and no fragmenting, or Kafka with a record over the broker's limit, is saying no retry can ever
/// send it. Under ServerlessWithOutbox the inline send and the durability pass move such an envelope to dead letter
/// storage and delete its outbox row, rather than leaving it for every later pass to send and fail again
/// </summary>
public class unsendable_outgoing_envelopes_are_dead_lettered_5047 : IAsyncDisposable
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly SerializationFailingProtocol theProtocol = new();
    private readonly DurableSendingAgent theAgent;

    public unsendable_outgoing_envelopes_are_dead_lettered_5047()
    {
        // Nothing listens on the port: the protocol below answers instead of a broker
        var endpoint = new TcpEndpoint(2345);
        var sender = new BatchedSender(endpoint, theProtocol, CancellationToken.None, NullLogger.Instance);

        theAgent = new DurableSendingAgent(sender, theRuntime.Options.Durability, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), Substitute.For<IMessageOutbox>(), endpoint);
        _disposables.Add(theAgent);

        theRuntime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri).Returns(theAgent);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private Envelope envelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = theAgent.Destination;
        envelope.Sender = theAgent;
        envelope.DeliverBy = null;
        return envelope;
    }

    private async Task<MessageContext> contextWithOutgoing(params Envelope[] envelopes)
    {
        var context = new MessageContext(theRuntime);

        // The handler's transaction (Marten, EF Core...) has already persisted the durable envelopes
        await context.EnlistInOutboxAsync(Substitute.For<IEnvelopeTransaction>());
        await context.As<IEnvelopeTransaction>().PersistOutgoingAsync(envelopes);

        return context;
    }

    private Task<DurabilityPassStepResult> recoverAsync(params Envelope[] envelopes)
    {
        // A recovered row has no sending agent of its own; the step finds it by destination
        foreach (var envelope in envelopes) envelope.Sender = null;

        var source = Substitute.For<IStaleOutgoingSource>();
        source.LoadStaleOutgoingAsync(default, default!, default, default)
            .ReturnsForAnyArgs(envelopes.ToList());

        var step = new RecoverStaleOutgoingStep(theRuntime.Storage, source, NullLogger.Instance);
        var context = new DurabilityPassContext(theRuntime, theRuntime.Storage, 10,
            DateTimeOffset.UtcNow.AddMinutes(1), () => DateTimeOffset.UtcNow);

        return step.ExecuteAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task the_inline_send_moves_an_envelope_the_transport_can_never_send_to_dead_letter_storage()
    {
        var accepted = envelope();
        var oversized = envelope();
        theProtocol.SerializationFailures.Add(oversized.Id);

        var context = await contextWithOutgoing(accepted, oversized);
        await context.SendAllOutgoingMessagesInlineAsync();

        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(oversized,
            Arg.Is<Exception>(e => e.Message.Contains("failed to serialize")));
        await theRuntime.Storage.Inbox.DidNotReceive().MoveToDeadLetterStorageAsync(accepted, Arg.Any<Exception>());

        // Both rows go in the one batched delete: the sent one and the dead lettered one
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 2 && x.Contains(accepted) && x.Contains(oversized)));

        theRuntime.MessageTracking.Received(1).MovedToErrorQueue(oversized, Arg.Any<Exception>());
    }

    [Fact]
    public async Task the_inline_send_leaves_any_other_failure_in_the_outbox()
    {
        var throttled = envelope();
        theProtocol.Rejected.Add(throttled.Id);

        var context = await contextWithOutgoing(throttled);
        await context.SendAllOutgoingMessagesInlineAsync();

        await theRuntime.Storage.Inbox.DidNotReceiveWithAnyArgs().MoveToDeadLetterStorageAsync(default!, default);
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task the_inline_send_leaves_the_outbox_row_when_the_dead_letter_cannot_be_written()
    {
        var oversized = envelope();
        theProtocol.SerializationFailures.Add(oversized.Id);
        theRuntime.Storage.Inbox.MoveToDeadLetterStorageAsync(oversized, Arg.Any<Exception>())
            .Returns(Task.FromException(new InvalidOperationException("database is down")));

        var context = await contextWithOutgoing(oversized);
        await context.SendAllOutgoingMessagesInlineAsync();

        // Nothing is lost: a later pass tries again
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    [Fact]
    public async Task the_durability_pass_moves_an_envelope_the_transport_can_never_send_to_dead_letter_storage()
    {
        var accepted = envelope();
        var oversized = envelope();
        theProtocol.SerializationFailures.Add(oversized.Id);

        var result = await recoverAsync(accepted, oversized);

        result.OutgoingSent.ShouldBe(1);
        result.OutgoingDeadLettered.ShouldBe(1);
        result.OutgoingFailed.ShouldBe(0);

        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(oversized, Arg.Any<Exception>());
        await theRuntime.Storage.Outbox.Received(1)
            .DeleteOutgoingAsync(Arg.Is<Envelope[]>(x => x.Length == 2 && x.Contains(accepted) && x.Contains(oversized)));
    }

    [Fact]
    public async Task the_durability_pass_leaves_any_other_failure_in_the_outbox()
    {
        var throttled = envelope();
        theProtocol.Rejected.Add(throttled.Id);

        var result = await recoverAsync(throttled);

        result.OutgoingFailed.ShouldBe(1);
        result.OutgoingDeadLettered.ShouldBe(0);
        await theRuntime.Storage.Inbox.DidNotReceiveWithAnyArgs().MoveToDeadLetterStorageAsync(default!, default);
        await theRuntime.Storage.Outbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(Arg.Any<Envelope[]>());
    }

    /// <summary>
    /// Answers for part of a batch the way SQS does with a message over its size limit: the accepted envelopes as
    /// one sub-batch, the ones it can never take as a serialization failure, and the throttled ones as a failure
    /// </summary>
    private class SerializationFailingProtocol : ISenderProtocol
    {
        public HashSet<Guid> SerializationFailures { get; } = new();
        public HashSet<Guid> Rejected { get; } = new();

        public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
        {
            var accepted = batch.Messages
                .Where(x => !SerializationFailures.Contains(x.Id) && !Rejected.Contains(x.Id)).ToList();
            var unsendable = batch.Messages.Where(x => SerializationFailures.Contains(x.Id)).ToList();
            var rejected = batch.Messages.Where(x => Rejected.Contains(x.Id)).ToList();

            if (accepted.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, accepted));
            }

            if (unsendable.Count > 0)
            {
                await callback.MarkSerializationFailureAsync(new OutgoingMessageBatch(batch.Destination, unsendable));
            }

            if (rejected.Count > 0)
            {
                await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, rejected),
                    new InvalidOperationException("Throttled"));
            }
        }
    }
}
