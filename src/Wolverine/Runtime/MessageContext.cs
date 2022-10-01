using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Util;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class MessageContext : MessagePublisher, IMessageContext, IEnvelopeTransaction, IEnvelopeLifecycle
{
    private IChannelCallback? _channel;
    private object? _sagaId;

    public MessageContext(IWolverineRuntime runtime) : base(runtime)
    {

    }

    internal IList<Envelope> Scheduled { get; } = new List<Envelope>();

    /// <summary>
    /// Discard all outstanding, cascaded messages and clear the transaction
    /// </summary>
    public async ValueTask ClearAllAsync()
    {
        Scheduled.Clear();
        _outstanding.Clear();

        if (Transaction != null)
        {
            await Transaction.RollbackAsync();
        }

        Transaction = null;
    }

    Task IEnvelopeTransaction.PersistAsync(Envelope envelope)
    {
        _outstanding.Fill(envelope);
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.PersistAsync(Envelope[] envelopes)
    {
        _outstanding.Fill(envelopes);
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.ScheduleJobAsync(Envelope envelope)
    {
        Scheduled.Fill(envelope);
        return Task.CompletedTask;
    }

    async Task IEnvelopeTransaction.CopyToAsync(IEnvelopeTransaction other)
    {
        await other.PersistAsync(_outstanding.ToArray());

        foreach (var envelope in Scheduled) await other.ScheduleJobAsync(envelope);
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
    }

    internal ValueTask ForwardScheduledEnvelopeAsync(Envelope envelope)
    {
        // TODO -- harden this a bit?
        envelope.Sender = Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination);
        envelope.Serializer = Runtime.Options.FindSerializer(envelope.ContentType);

        return persistOrSendAsync(envelope);
    }

    /// <summary>
    ///     Send a response message back to the original sender of the message being handled.
    ///     This can only be used from within a message handler
    /// </summary>
    /// <param name="response"></param>
    /// <param name="context"></param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <returns></returns>
    public ValueTask RespondToSenderAsync(object response)
    {
        if (Envelope == null)
        {
            throw new InvalidOperationException(
                "This operation can only be performed while in the middle of handling an incoming message");
        }

        if (Envelope.ReplyUri == null)
        {
            throw new ArgumentOutOfRangeException(nameof(Envelope), $"There is no {nameof(Envelope.ReplyUri)}");
        }

        return SendAsync(Envelope.ReplyUri, response);
    }


    public async Task EnqueueCascadingAsync(object? message)
    {
        if (Envelope == null) throw new InvalidOperationException("No Envelope attached to this context");

        if (Envelope.ResponseType != null && (message?.GetType() == Envelope.ResponseType ||
                                              Envelope.ResponseType.IsAssignableFrom(message?.GetType())))
        {
            Envelope.Response = message;
            return;
        }

        switch (message)
        {
            case null:
                return;

            case ISendMyself sendsMyself:
                await sendsMyself.ApplyAsync(this);
                return;

            case Envelope _:
                throw new InvalidOperationException(
                    "You cannot directly send an Envelope. You may want to use ISendMyself for cascading messages");
                return;

            case IEnumerable<object> enumerable:
                foreach (var o in enumerable) await EnqueueCascadingAsync(o);

                return;
        }

        if (message.GetType().ToMessageTypeName() == Envelope.ReplyRequested)
        {
            await SendAsync(Envelope.ReplyUri!, message, new DeliveryOptions{IsResponse = true});
            return;
        }


        await PublishAsync(message);
    }

    public Envelope? Envelope { get; protected set; }


    public async Task FlushOutgoingMessagesAsync()
    {
        if (Envelope.ReplyRequested.IsNotEmpty() && Outstanding.All(x => x.MessageType != Envelope.ReplyRequested))
        {
            await SendFailureAcknowledgementAsync($"No response was created for expected response '{Envelope.ReplyRequested}'");
        }
        
        if (!Outstanding.Any()) return;

        foreach (var envelope in Outstanding)
        {
            try
            {
                await envelope.QuickSendAsync();
            }
            catch (Exception e)
            {
                Runtime.Logger.LogError(e,
                    "Unable to send an outgoing message, most likely due to serialization issues");
                Runtime.MessageLogger.DiscardedEnvelope(envelope);
            }
        }

        if (ReferenceEquals(Transaction, this))
        {
            await flushScheduledMessagesAsync();
        }

        _outstanding.Clear();
    }

    public ValueTask CompleteAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        return _channel.CompleteAsync(Envelope);
    }

    public ValueTask DeferAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        return _channel.DeferAsync(Envelope);
    }

    public async Task ReScheduleAsync(DateTimeOffset scheduledTime)
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        Envelope.ScheduledTime = scheduledTime;
        if (_channel is ISupportNativeScheduling c)
        {
            await c.MoveToScheduledUntilAsync(Envelope, Envelope.ScheduledTime.Value);
        }
        else
        {
            await Persistence.ScheduleJobAsync(Envelope);
        }
    }

    public async Task MoveToDeadLetterQueueAsync(Exception exception)
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        if (_channel is ISupportDeadLetterQueue c)
        {
            await c.MoveToErrorsAsync(Envelope, exception);
        }
        else
        {
            // If persistable, persist
            await Persistence.MoveToDeadLetterStorageAsync(Envelope, exception);
        }
    }

    public Task RetryExecutionNowAsync()
    {
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        return Runtime.Pipeline.InvokeAsync(Envelope, _channel!);
    }

    internal void ClearState()
    {
        _outstanding.Clear();
        Scheduled.Clear();
        Envelope = null;
        Transaction = null;
        _sagaId = null;
    }

    internal void ReadEnvelope(Envelope? originalEnvelope, IChannelCallback channel)
    {
        Envelope = originalEnvelope ?? throw new ArgumentNullException(nameof(originalEnvelope));
        CorrelationId = originalEnvelope.CorrelationId;
        ConversationId = originalEnvelope.Id;
        _channel = channel;
        _sagaId = originalEnvelope.SagaId;

        Transaction = this;

        if (Envelope.AckRequested && Envelope.ReplyUri != null)
        {
            var ack = new Acknowledgement { RequestId = Envelope.Id };
            var ackEnvelope = Runtime.RoutingFor(typeof(Acknowledgement)).RouteToDestination(ack, Envelope.ReplyUri, null);
            trackEnvelopeCorrelation(ackEnvelope);
            _outstanding.Add(ackEnvelope);
        }
    }

    private async Task flushScheduledMessagesAsync()
    {
        if (Persistence is NullEnvelopePersistence)
        {
            foreach (var envelope in Scheduled) Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime!.Value, envelope);
        }
        else
        {
            foreach (var envelope in Scheduled) await Persistence.ScheduleJobAsync(envelope);
        }

        Scheduled.Clear();
    }

    protected override void trackEnvelopeCorrelation(Envelope outbound)
    {
        base.trackEnvelopeCorrelation(outbound);
        outbound.SagaId = _sagaId?.ToString() ?? Envelope?.SagaId ?? outbound.SagaId;

        if (Envelope != null)
        {
            outbound.ConversationId = Envelope.Id;
        }
    }

    public async ValueTask SendAcknowledgementAsync()
    {
        if (Envelope!.ReplyUri == null) return;

        var acknowledgement = new Acknowledgement
        {
            RequestId = Envelope.Id
        };

        var envelope = Runtime.RoutingFor(typeof(Acknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        trackEnvelopeCorrelation(envelope);
        envelope.SagaId = Envelope.SagaId;
        // TODO -- reevaluate the metadata. Causation, Originator, all that

        try
        {
            await envelope.StoreAndForwardAsync();
        }
        catch (Exception e)
        {
            // TODO -- any kind of retry? Only an issue for inline senders anyway
            Runtime.Logger.LogError(e, "Failure while sending an acknowledgement for envelope {Id}", envelope.Id);
        }
    }

    public async ValueTask SendFailureAcknowledgementAsync(string failureDescription)
    {
        if (Envelope!.ReplyUri == null) return;

        var acknowledgement = new FailureAcknowledgement
        {
            RequestId = Envelope.Id,
            Message = failureDescription
        };

        var envelope = Runtime.RoutingFor(typeof(FailureAcknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        trackEnvelopeCorrelation(envelope);
        envelope.SagaId = Envelope.SagaId;
        // TODO -- reevaluate the metadata. Causation, ORiginator, all that

        try
        {
            await envelope.StoreAndForwardAsync();
        }
        catch (Exception e)
        {
            // TODO -- any kind of retry? Only an issue for inline senders anyway
            Runtime.Logger.LogError(e, "Failure while sending a failure acknowledgement for envelope {Id}", envelope.Id);
        }
    }
}
