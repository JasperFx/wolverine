using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

/// <summary>
/// IEnvelopeLifecycle adapter for outgoing envelopes during send failure handling.
/// Allows existing IContinuation implementations to operate on outgoing envelopes.
/// </summary>
internal class SendingEnvelopeLifecycle : IEnvelopeLifecycle
{
    private readonly MessageBus _bus;
    private readonly ISendingAgent _agent;
    private readonly IMessageOutbox? _outbox;

    public SendingEnvelopeLifecycle(Envelope envelope, IWolverineRuntime runtime, ISendingAgent agent,
        IMessageOutbox? outbox)
    {
        Envelope = envelope;
        _bus = new MessageBus(runtime);
        _agent = agent;
        _outbox = outbox;
    }

    public Envelope? Envelope { get; }

    /// <summary>
    /// For sending failures, "complete" means discard the envelope.
    /// Delete from outbox if durable, otherwise just drop it.
    /// </summary>
    public async ValueTask CompleteAsync()
    {
        if (_outbox != null && Envelope != null)
        {
            await _outbox.DeleteOutgoingAsync(Envelope);
        }
    }

    /// <summary>
    /// Re-enqueue for retry through the sending agent.
    /// </summary>
    public async ValueTask DeferAsync()
    {
        if (Envelope != null)
        {
            await _agent.EnqueueOutgoingAsync(Envelope);
        }
    }

    /// <summary>
    /// Schedule the envelope for later retry via the outbox.
    /// </summary>
    public async Task ReScheduleAsync(DateTimeOffset scheduledTime)
    {
        if (Envelope != null)
        {
            Envelope.ScheduledTime = scheduledTime;
            Envelope.Status = EnvelopeStatus.Scheduled;

            if (_outbox != null)
            {
                await _outbox.StoreOutgoingAsync(Envelope, Envelope.OwnerId);
            }
        }
    }

    /// <summary>
    /// Move the failed outgoing envelope to dead letter storage.
    /// </summary>
    public async Task MoveToDeadLetterQueueAsync(Exception exception)
    {
        if (Envelope != null)
        {
            await _bus.Runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(Envelope, exception);
        }
    }

    /// <summary>
    /// Re-enqueue immediately for retry.
    /// </summary>
    public async Task RetryExecutionNowAsync()
    {
        if (Envelope != null)
        {
            await _agent.EnqueueOutgoingAsync(Envelope);
        }
    }

    // Not applicable for outgoing messages
    public ValueTask SendAcknowledgementAsync() => ValueTask.CompletedTask;
    public ValueTask SendFailureAcknowledgementAsync(string failureDescription) => ValueTask.CompletedTask;
    public ValueTask RespondToSenderAsync(object response) => ValueTask.CompletedTask;

    // No transaction context for sending failures, so flushing is a no-op
    public Task FlushOutgoingMessagesAsync() => Task.CompletedTask;

    // Delegate IMessageBus methods to inner MessageBus
    public string? TenantId
    {
        get => _bus.TenantId;
        set => _bus.TenantId = value;
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeAsync(message, cancellation, timeout);

    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeAsync(message, options, cancellation, timeout);

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeAsync<T>(message, cancellation, timeout);

    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeAsync<T>(message, options, cancellation, timeout);

    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeForTenantAsync(tenantId, message, cancellation, timeout);

    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = default)
        => _bus.InvokeForTenantAsync<T>(tenantId, message, cancellation, timeout);

    public IDestinationEndpoint EndpointFor(string endpointName) => _bus.EndpointFor(endpointName);
    public IDestinationEndpoint EndpointFor(Uri uri) => _bus.EndpointFor(uri);

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => _bus.PreviewSubscriptions(message);
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => _bus.PreviewSubscriptions(message, options);

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => _bus.SendAsync(message, options);
    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) => _bus.PublishAsync(message, options);
    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
        => _bus.BroadcastToTopicAsync(topicName, message, options);
}
