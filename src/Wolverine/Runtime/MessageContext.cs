using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using JasperFx;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Runtime;

public class MessageContext : MessageBus, IMessageContext, IEnvelopeTransaction, IEnvelopeLifecycle
{
    private IChannelCallback? _channel;

    private bool _hasFlushed;
    private object? _sagaId;

    public MessageContext(IWolverineRuntime runtime) : base(runtime)
    {
    }

    // Used implicitly in codegen
    public MessageContext(IWolverineRuntime runtime, string tenantId) : base(runtime)
    {
        TenantId = tenantId;
    }

    internal IList<Envelope> Scheduled { get; } = new List<Envelope>();

    private bool hasRequestedReply()
    {
        return Envelope != null && Envelope.ReplyRequested.IsNotEmpty();
    }

    private bool isMissingRequestedReply()
    {
        return Outstanding.All(x => x.MessageType != Envelope!.ReplyRequested);
    }

    public async Task FlushOutgoingMessagesAsync()
    {
        if (_hasFlushed)
        {
            return;
        }

        if (hasRequestedReply() && _channel is not InvocationCallback && isMissingRequestedReply())
        {
            await SendFailureAcknowledgementAsync(
                $"No response was created for expected response '{Envelope.ReplyRequested}'");
        }

        if (!Outstanding.Any())
        {
            return;
        }

        foreach (var envelope in Outstanding)
        {
            try
            {
                if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
                {
                    if (!envelope.Sender!.IsDurable)
                    {
                        Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime!.Value, envelope);
                    }
                }
                else if (ReferenceEquals(this, Transaction))
                {
                    await envelope.StoreAndForwardAsync();
                }
                else
                {
                    await envelope.QuickSendAsync();
                }
            }
            catch (Exception e)
            {
                Runtime.Logger.LogError(e,
                    "Unable to send an outgoing message, most likely due to serialization issues");
                Runtime.MessageTracking.DiscardedEnvelope(envelope);
            }
        }

        if (ReferenceEquals(Transaction, this))
        {
            await flushScheduledMessagesAsync();
        }

        _outstanding.Clear();

        _hasFlushed = true;
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

        Runtime.MessageTracking.Requeued(Envelope);
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
            await Storage.Inbox.ScheduleJobAsync(Envelope);
        }
    }

    public async Task MoveToDeadLetterQueueAsync(Exception exception)
    {
        // Don't bother with agent commands
        if (Envelope?.Message is IAgentCommand) return;
        
        if (_channel == null || Envelope == null)
        {
            throw new InvalidOperationException("No Envelope is active for this context");
        }

        if (_channel is ISupportDeadLetterQueue c && c.NativeDeadLetterQueueEnabled)
        {
            if (Envelope.Batch != null)
            {
                foreach (var envelope in Envelope.Batch)
                {
                    await c.MoveToErrorsAsync(envelope, exception);
                }
            }
            else
            {
                await c.MoveToErrorsAsync(Envelope, exception);
            }

            return;
        }
        
        if (Envelope.Batch != null)
        {
            foreach (var envelope in Envelope.Batch)
            {
                await Storage.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
            }
        }
        else
        {
            // If persistable, persist
            await Storage.Inbox.MoveToDeadLetterStorageAsync(Envelope, exception);
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

    public async ValueTask SendAcknowledgementAsync()
    {
        if (Envelope!.ReplyUri == null)
        {
            return;
        }

        var acknowledgement = new Acknowledgement
        {
            RequestId = Envelope.Id
        };

        var envelope = Runtime.RoutingFor(typeof(Acknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        TrackEnvelopeCorrelation(envelope, Activity.Current);
        envelope.SagaId = Envelope.SagaId;

        try
        {
            await envelope.StoreAndForwardAsync();
        }
        catch (Exception e)
        {
            // This should never happen because all the sending agents catch errors, but you know...
            Runtime.Logger.LogError(e, "Failure while sending an acknowledgement for envelope {Id}", envelope.Id);
        }
    }

    public async ValueTask SendFailureAcknowledgementAsync(string failureDescription)
    {
        if (Envelope!.ReplyUri == null)
        {
            return;
        }

        var acknowledgement = new FailureAcknowledgement
        {
            RequestId = Envelope.Id,
            Message = failureDescription
        };

        var envelope = Runtime.RoutingFor(typeof(FailureAcknowledgement))
            .RouteToDestination(acknowledgement, Envelope.ReplyUri, null);

        TrackEnvelopeCorrelation(envelope, Activity.Current);
        envelope.SagaId = Envelope.SagaId;

        try
        {
            await envelope.StoreAndForwardAsync();
        }
        catch (NotSupportedException)
        {
            // I don't like this, but if this happens, then it should never have been routed to the failure ack
        }
        catch (Exception e)
        {
            // Should never happen, but still.
            Runtime.Logger.LogError(e, "Failure while sending a failure acknowledgement for envelope {Id}",
                envelope.Id);
        }
    }

    Task IEnvelopeTransaction.PersistOutgoingAsync(Envelope envelope)
    {
        _outstanding.Fill(envelope);
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.PersistOutgoingAsync(Envelope[] envelopes)
    {
        _outstanding.Fill(envelopes);
        return Task.CompletedTask;
    }

    Task IEnvelopeTransaction.PersistIncomingAsync(Envelope envelope)
    {
        if (envelope.Status == EnvelopeStatus.Scheduled)
        {
            Scheduled.Fill(envelope);
        }

        return Task.CompletedTask;
    }

    public ValueTask RollbackAsync()
    {
        return ValueTask.CompletedTask;
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

        return EndpointFor(Envelope.ReplyUri).SendAsync(response);
    }

    public Envelope? Envelope { get; protected set; }

    internal async Task CopyToAsync(IEnvelopeTransaction other)
    {
        await other.PersistOutgoingAsync(_outstanding.ToArray());

        foreach (var envelope in Scheduled) await other.PersistIncomingAsync(envelope);
    }

    /// <summary>
    ///     Discard all outstanding, cascaded messages and clear the transaction
    /// </summary>
    public async ValueTask ClearAllAsync()
    {
        Scheduled.Clear();
        _outstanding.Clear();

        if (Transaction != null)
        {
            try
            {
                await Transaction.RollbackAsync();
            }
            catch (Exception)
            {
                // Swallowing these exceptions as they can do nothing but harm
            }
        }

        Transaction = null;
    }

    internal ValueTask ForwardScheduledEnvelopeAsync(Envelope envelope)
    {
        if (envelope.Destination == null)
        {
            throw new InvalidOperationException($"{nameof(Envelope.Destination)} is missing");
        }

        if (envelope.ContentType == null)
        {
            throw new InvalidOperationException("${nameof(Envelope.ContentType} is missing");
        }

        envelope.Sender = Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination);
        envelope.Serializer = Runtime.Options.FindSerializer(envelope.ContentType);

        if (envelope.Serializer == null)
        {
            throw new InvalidOperationException($"Invalid content type '{envelope.ContentType}'");
        }

        return PersistOrSendAsync(envelope);
    }

    public async Task EnqueueCascadingAsync(object? message)
    {
        if (message is ISideEffect)
        {
            throw new InvalidOperationException(
                $"Message of type {message.GetType().FullNameInCode()} implements {typeof(ISideEffect).FullNameInCode()}, and cannot be used as a cascading message. Side effects cannot be mixed in with outgoing cascaded messages.");
            
        }
        
        if (Envelope?.ResponseType != null && (message?.GetType() == Envelope.ResponseType ||
                                               Envelope.ResponseType.IsInstanceOfType(message)))
        {
            Envelope.Response = message;

            if (Runtime.Options.Durability.Mode == DurabilityMode.MediatorOnly) return;

            // This was done specifically for the HTTP transport's optimized 
            // request/reply mechanism
            if (Envelope.DoNotCascadeResponse) return;
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

            case IEnumerable<object> enumerable:
                foreach (var o in enumerable) await EnqueueCascadingAsync(o);

                return;

            case IAsyncEnumerable<object> asyncEnumerable:
                await foreach (var o in asyncEnumerable) await EnqueueCascadingAsync(o);

                return;
        }

        if (Envelope?.ReplyUri != null && message.GetType().ToMessageTypeName() == Envelope.ReplyRequested)
        {
            await EndpointFor(Envelope.ReplyUri!).SendAsync(message, new DeliveryOptions { IsResponse = true });
            return;
        }

        await PublishAsync(message);
    }

    internal void ClearState()
    {
        if (Transaction is IDisposable d)
        {
            d.SafeDispose();
        }

        _hasFlushed = false;

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
        TenantId = originalEnvelope.TenantId;

        Transaction = this;

        if (Envelope.AckRequested && Envelope.ReplyUri != null)
        {
            var ack = new Acknowledgement { RequestId = Envelope.Id };
            var ackEnvelope = Runtime.RoutingFor(typeof(Acknowledgement))
                .RouteToDestination(ack, Envelope.ReplyUri, null);
            TrackEnvelopeCorrelation(ackEnvelope, Activity.Current);
            _outstanding.Add(ackEnvelope);
        }
    }

    private async Task flushScheduledMessagesAsync()
    {
        if (Storage is NullMessageStore)
        {
            foreach (var envelope in Scheduled)
                Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime!.Value, envelope);
        }
        else
        {
            foreach (var envelope in Scheduled) await Storage.Inbox.ScheduleJobAsync(envelope);
        }

        Scheduled.Clear();
    }

    internal override void TrackEnvelopeCorrelation(Envelope outbound, Activity? activity)
    {
        base.TrackEnvelopeCorrelation(outbound, activity);
        outbound.SagaId = _sagaId?.ToString() ?? Envelope?.SagaId ?? outbound.SagaId;

        if (ConversationId != Guid.Empty)
        {
            outbound.ConversationId = ConversationId;
        }

        if (Envelope != null)
        {
            outbound.ConversationId = Envelope.ConversationId == Guid.Empty ? Envelope.Id : Envelope.ConversationId;
        }
    }

    public void OverrideStorage(IMessageStore messageStore)
    {
        Storage = messageStore;
    }
}