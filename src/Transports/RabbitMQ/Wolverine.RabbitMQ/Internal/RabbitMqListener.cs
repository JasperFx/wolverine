using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

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
        await _sendBlock.PostAsync(envelope);
    }

    public bool NativeDeadLetterQueueEnabled => true;
}

internal class RabbitMqListener : RabbitMqChannelAgent, IListener, ISupportDeadLetterQueue
{
    private readonly IChannelCallback _callback;
    private readonly CancellationToken _cancellation = CancellationToken.None;
    private WorkerQueueMessageConsumer? _consumer;
    private readonly ISupportDeadLetterQueue _deadLetterQueueCallback;
    private readonly IWolverineRuntime _runtime;
    private readonly RabbitMqTransport _transport;
    private readonly IReceiver _receiver;
    private readonly Lazy<RabbitMqSender> _sender;

    public RabbitMqListener(IWolverineRuntime runtime,
        RabbitMqQueue queue, RabbitMqTransport transport, IReceiver receiver) : base(transport.ListeningConnection,
        runtime.LoggerFactory.CreateLogger<RabbitMqListener>())
    {
        Queue = queue;
        Address = queue.Uri;

        _sender = new Lazy<RabbitMqSender>(() => Queue.ResolveSender(runtime));
        _cancellation.Register(() =>
        {
            _ = teardownChannel();
        });

        _runtime = runtime;
        _transport = transport;
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

        _callback = (Queue.DeadLetterQueue != null) &
                    (Queue.DeadLetterQueue?.Mode == DeadLetterQueueMode.InteropFriendly)
            ? new RabbitMqInteropFriendlyCallback(_transport, _transport.Queues[Queue.DeadLetterQueue!.QueueName],
                _runtime)
            : _transport.Callback!;

        _deadLetterQueueCallback = _callback.As<ISupportDeadLetterQueue>();
        // Need to disable this if using WolverineStorage
        NativeDeadLetterQueueEnabled = queue.DeadLetterQueue != null &&
                                       queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage;

        if (transport.AutoPingListeners)
        {
            // This is trying to be a forcing function to make the channel really connect
            var ping = Envelope.ForPing(Address);
            Task.Run(() => _sender.Value.SendAsync(ping));
        }
    }

    public async Task CreateAsync()
    {
        await EnsureConnected();
        
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
                Logger.LogInformation("{Count} messages in queue {QueueName} at listening start up time", result.MessageCount, Queue.QueueName);
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Unable to check the queued count for {QueueName}", Queue.QueueName);
        }

        var mapper = Queue.BuildMapper(_runtime);

        _consumer = new WorkerQueueMessageConsumer(Channel!, _receiver, Logger, this, mapper, Address,
            _cancellation);

        await Channel!.BasicQosAsync(0, Queue.PreFetchCount, false, _cancellation);
        await Channel.BasicConsumeAsync(_consumer, Queue.QueueName, false, _transport.ConnectionFactory.ClientProvidedName);
    }

    public RabbitMqQueue Queue { get; }

    public async ValueTask StopAsync()
    {
        if (_consumer == null)
        {
            return;
        }

        foreach (var consumerTag in _consumer.ConsumerTags) await Channel!.BasicCancelAsync(consumerTag, noWait: true, cancellationToken: default);
    }

    public override async ValueTask DisposeAsync()
    {
        _receiver.Dispose();
        await base.DisposeAsync();

        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync();
        }
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

    public override string ToString()
    {
        return $"RabbitMqListener: {Address}";
    }

    public Uri Address { get; }

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

    public async ValueTask RequeueAsync(RabbitMqEnvelope envelope)
    {
        if (!envelope.Acknowledged)
        {
            await Channel.BasicNackAsync(envelope.DeliveryTag, false, false, _cancellation);
        }

        await _sender.Value.SendAsync(envelope);
    }

    public async Task CompleteAsync(ulong deliveryTag)
    {
        await Channel!.BasicAckAsync(deliveryTag, true, _cancellation);
    }
}
