using JasperFx.Blocks;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqInteropFriendlyCallback : IChannelCallback, ISupportDeadLetterQueue
{
    private readonly IChannelCallback _inner;
    private readonly RetryBlock<Envelope> _sendBlock;


    public RabbitMqInteropFriendlyCallback(RabbitMqTransport transport, RabbitMqQueue deadLetterQueue,
        IWolverineRuntime runtime)
    {
        _inner = transport.Callback!;
        var sender = deadLetterQueue.ResolveSender(runtime);

        _sendBlock =
            new RetryBlock<Envelope>((e, _) => sender.SendAsync(e).AsTask(), runtime.Logger, runtime.Cancellation);
    }

    public IHandlerPipeline? Pipeline => _inner.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return _inner.CompleteAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return _inner.DeferAsync(envelope);
    }

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        await _sendBlock.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => true;
}

internal class RabbitMqListener : RabbitMqChannelAgent, IListener, ISupportDeadLetterQueue, ISupportMultipleConsumers
{
    private readonly IChannelCallback _callback;
    private readonly CancellationToken _cancellation = CancellationToken.None;
    private readonly ISupportDeadLetterQueue _deadLetterQueueCallback;
    private readonly IReceiver _receiver;
    private readonly IWolverineRuntime _runtime;
    private readonly Lazy<ISender> _sender;
    private readonly RabbitMqTransport _transport;

    // Serializes the three rebuild triggers -- connection recovery, an unexpected channel-only
    // shutdown (#3171), and a channel callback exception (#3391). A burst of callback exceptions
    // otherwise races two rebuilds onto the same channel and leaves duplicate consumers behind.
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private WorkerQueueMessageConsumer? _consumer;
    private string? _consumerId;

    public RabbitMqListener(IWolverineRuntime runtime,
        RabbitMqQueue queue, RabbitMqTransport transport, IReceiver receiver) : base(
        transport.UseSenderConnectionOnly ? transport.SendingConnection : transport.ListeningConnection,
        runtime.LoggerFactory.CreateLogger<RabbitMqListener>())
    {
        Queue = queue;
        Address = queue.Uri;
        ConsumerAddress = Address;

        _sender = new Lazy<ISender>(() => Queue.ResolveSender(runtime));
        _cancellation.Register(() => { _ = teardownChannel(); });

        _runtime = runtime;
        _transport = transport;
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

        var useEnhancedOrInterop = Queue.DeadLetterQueue != null &&
                                    (Queue.DeadLetterQueue.Mode == DeadLetterQueueMode.InteropFriendly ||
                                     _transport.UseEnhancedDeadLettering);

        _callback = useEnhancedOrInterop
            ? new RabbitMqInteropFriendlyCallback(_transport, _transport.Queues[Queue.DeadLetterQueue!.QueueName],
                _runtime)
            : _transport.Callback!;

        _deadLetterQueueCallback = _callback.As<ISupportDeadLetterQueue>();
        // Need to disable this if using WolverineStorage
        NativeDeadLetterQueueEnabled = queue.DeadLetterQueue != null &&
                                       queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage;
    }

    public RabbitMqQueue Queue { get; }

    protected override ushort? ConsumerDispatchConcurrency => Queue.ConsumerDispatchConcurrency;

    public async ValueTask StopAsync()
    {
        var consumer = _consumer;
        if (consumer == null)
        {
            return;
        }

        var channel = Channel;
        if (channel != null)
        {
            foreach (var consumerTag in consumer.ConsumerTags)
            {
                await channel.BasicCancelAsync(consumerTag, true, default);
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _consumer?.Dispose();
        _consumer = null;

        await base.DisposeAsync();
        
        // Don't dispose _sender.Value — it's a shared sender cached on
        // RabbitMqQueue and reused across listener pause/restart cycles.
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (envelope is not RabbitMqEnvelope e)
        {
            return false;
        }

        await e.RabbitMqListener.RequeueAsync(e);
        return true;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return _callback.CompleteAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return _callback.DeferAsync(envelope);
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        return _deadLetterQueueCallback.MoveToErrorsAsync(envelope, exception);
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    public string? ConsumerId
    {
        get => _consumerId;
        set
        {
            _consumerId = value;

            if (value == null)
            {
                ConsumerAddress = Address;
            }
            else
            {
                ConsumerAddress = new Uri($"{Address}?consumer={_consumerId}");
            }
        }
    }

    public Uri BaseAddress => Queue.Uri;
    public Uri ConsumerAddress { get; private set; }

    public async Task CreateAsync()
    {
        await EnsureInitiated();

        if (Queue.AutoDelete || _transport.AutoProvision)
        {
            await Queue.DeclareAsync(Channel!, Logger);

            if (Queue.DeadLetterQueue != null && Queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage)
            {
                var dlq = _transport.Queues[Queue.DeadLetterQueue.QueueName];
                await dlq.DeclareAsync(Channel!, Logger);
            }
        }

        try
        {
            var result = await Channel!.QueueDeclarePassiveAsync(Queue.QueueName, _cancellation);
            if (Queue.Role == EndpointRole.Application)
            {
                Logger.LogInformation("{Count} messages in queue {QueueName} at listening start up time",
                    result.MessageCount, Queue.QueueName);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Unable to check the queued count for {QueueName}", Queue.QueueName);
        }

        var mapper = Queue.BuildMapper(_runtime);

        _consumer = new WorkerQueueMessageConsumer(Channel!, _receiver, Logger, this, mapper, Address, _cancellation);

        await Channel!.BasicQosAsync(0, Queue.PreFetchCount, false, _cancellation);
        await Channel.BasicConsumeAsync(Queue.QueueName, false,
            _transport.ConnectionFactory?.ClientProvidedName ?? _runtime.Options.ServiceName, Queue.ConsumerArguments, _consumer,
            _runtime.Cancellation);

        if (_transport.AutoPingListeners)
        {
            // This is trying to be a forcing function to make the channel really connect
            var ping = Envelope.ForPing(Address);
            await _sender.Value.SendAsync(ping);
        }
    }

    /// <summary>
    /// Eagerly rebuild the listener after an unexpected channel-only shutdown where the
    /// connection is still alive (#3171). A listener sits blocked on the broker and never
    /// calls EnsureInitiated again on its own, so unlike a sender it cannot heal lazily —
    /// we re-declare and re-consume here. If the whole connection dropped, we let the
    /// ConnectionMonitor recovery path own the rebuild to avoid a double restart.
    /// </summary>
    protected override void HandleUnexpectedChannelShutdown()
    {
        if (!ConnectionIsLive)
        {
            return;
        }

#pragma warning disable VSTHRD110
        Task.Run(async () =>
#pragma warning restore VSTHRD110
        {
            try
            {
                await ReconnectedAsync();
                Logger.LogInformation(
                    "Rebuilt the Rabbit MQ listener at {Uri} after an unexpected channel shutdown", Address);
            }
            catch (Exception e)
            {
                Logger.LogError(e,
                    "Error trying to rebuild the Rabbit MQ listener at {Uri} after an unexpected channel shutdown",
                    Address);
            }
        });
    }

    /// <summary>
    /// Defer the callback-exception eager restart to the full listener rebuild (#3391). The base
    /// implementation only swaps in a fresh channel, which is enough for a sender but leaves a
    /// listener sitting on an open channel with ZERO consumers while reporting Connected — a
    /// silently dead listener. ReconnectedAsync already does the correct work (cancel any surviving
    /// consumer, tear the channel down, re-declare and re-consume), so we route through it rather
    /// than duplicating a consume path here.
    /// </summary>
    protected override Task restartAfterCallbackExceptionAsync()
    {
        return ReconnectedAsync();
    }

    internal override async Task ReconnectedAsync()
    {
        await _reconnectLock.WaitAsync();
        try
        {
            try
            {
                await StopAsync();
            }
            catch (Exception e)
            {
                // The channel may already be dead, in which case cancelling the consumers throws.
                // That's fine — we're about to tear it down and rebuild anyway.
                Logger.LogDebug(e,
                    "Error cancelling consumers on a dead channel for {Uri} during reconnect; continuing with rebuild",
                    Address);
            }

            await teardownChannel();
            await CreateAsync();

            await base.ReconnectedAsync();
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    public override string ToString()
    {
        return $"RabbitMqListener: {Address}";
    }

    /// <summary>
    /// True only when this delivery can still legitimately be settled -- that is, when the channel it
    /// arrived on is the channel we currently hold AND that channel is still open.
    ///
    /// A bare null check is NOT sufficient here, and using one would silently lose messages. Delivery
    /// tags are scoped to a single channel and restart at 1 on every new one, so a stale tag replayed
    /// against a rebuilt channel addresses a completely different delivery -- and because CompleteAsync
    /// acks with multiple: true, that would ack every LOWER tag on the new channel as well, including
    /// messages still in the handler pipeline.
    ///
    /// When this returns false the correct behavior is to do nothing at all. The broker never saw an
    /// ack, so it requeues the delivery on channel close and redelivers it; the durable inbox then
    /// deduplicates it. Retrying the settle instead -- which is what the RetryBlock used to do after
    /// `Channel!` threw a NullReferenceException mid-reconnect -- can never succeed on any later
    /// channel, and just burns the retry budget while the same message cycles round again.
    /// </summary>
    internal bool CanSettle(RabbitMqEnvelope envelope)
    {
        var channel = Channel;
        if (channel is null || !ReferenceEquals(channel, envelope.DeliveredOn) || !channel.IsOpen)
        {
            Logger.LogDebug(
                "Discarding an unsettleable Rabbit MQ delivery tag {DeliveryTag} at {Uri}; the channel it arrived on was closed or replaced. The broker will redeliver and the durable inbox will deduplicate.",
                envelope.DeliveryTag, Address);

            return false;
        }

        return true;
    }

    public async ValueTask RequeueAsync(RabbitMqEnvelope envelope)
    {
        if (!envelope.Acknowledged && CanSettle(envelope))
        {
            await envelope.DeliveredOn.BasicNackAsync(envelope.DeliveryTag, false, false, _cancellation);
        }

        await _sender.Value.SendAsync(envelope);
    }

    public Task CompleteAsync(RabbitMqEnvelope envelope)
    {
        if (!CanSettle(envelope))
        {
            return Task.CompletedTask;
        }

        // NOTE: multiple: true also acks every LOWER delivery tag on this channel. That is wrong
        // under a concurrent listener -- it acks messages still in the handler pipeline -- but it
        // is currently load-bearing, because it sweeps up deliveries that some settle paths never
        // acknowledge at all. Changing it in isolation leaks those deliveries. Tracked separately;
        // see GH-3492 and the ack-semantics branch.
        //
        // Coalescing these into cumulative acks behind a batching window was measured and REJECTED:
        // basic.ack is a fire-and-forget frame, not an RPC, so batching saves no round trips while
        // the extra channel hops cost ~10% of max inline throughput. See the ledger in
        // RABBITMQ-PERF-DEEP-DIVE-PLAN.md.
        return envelope.DeliveredOn.BasicAckAsync(envelope.DeliveryTag, true, _cancellation).AsTask();
    }
}
