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

internal class RabbitMqListener : RabbitMqConnectionAgent, IListener, ISupportDeadLetterQueue
{
    private readonly IChannelCallback _callback;
    private readonly CancellationToken _cancellation = CancellationToken.None;
    private readonly WorkerQueueMessageConsumer? _consumer;
    private readonly ISupportDeadLetterQueue _deadLetterQueueCallback;
    private readonly IReceiver _receiver;
    private readonly RabbitMqSender _sender;

    public RabbitMqListener(IWolverineRuntime runtime,
        RabbitMqQueue queue, RabbitMqTransport transport, IReceiver receiver) : base(transport.ListeningConnection,
        runtime.LoggerFactory.CreateLogger<RabbitMqListener>())
    {
        Queue = queue;
        Address = queue.Uri;

        _sender = Queue.ResolveSender(runtime);
        _cancellation.Register(teardownChannel);

        EnsureConnected();

        if (queue.AutoDelete || transport.AutoProvision)
        {
            queue.Declare(Channel!, Logger);

            if (queue.DeadLetterQueue != null && queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage)
            {
                var dlq = transport.Queues[queue.DeadLetterQueue.QueueName];
                dlq.Declare(Channel!, Logger);
            }
        }


        try
        {
            var result = Channel!.QueueDeclarePassive(queue.QueueName);
            Logger.LogInformation("{Count} messages in queue {QueueName} at listening start up time", result.MessageCount, queue.QueueName);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Unable to check the queued count for {QueueName}", queue.QueueName);
        }

        var mapper = queue.BuildMapper(runtime);

        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _consumer = new WorkerQueueMessageConsumer(Channel!, receiver, Logger, this, mapper, Address,
            _cancellation);

        Channel!.BasicQos(0, Queue.PreFetchCount, false);
        Channel.BasicConsume(_consumer, queue.QueueName);

        _callback = (queue.DeadLetterQueue != null) &
                    (queue.DeadLetterQueue?.Mode == DeadLetterQueueMode.InteropFriendly)
            ? new RabbitMqInteropFriendlyCallback(transport, transport.Queues[queue.DeadLetterQueue!.QueueName],
                runtime)
            : transport.Callback!;

        _deadLetterQueueCallback = _callback.As<ISupportDeadLetterQueue>();
        // Need to disable this if using WolverineStorage
        NativeDeadLetterQueueEnabled = queue.DeadLetterQueue != null &&
                                       queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage;
    }

    public RabbitMqQueue Queue { get; }

    public ValueTask StopAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
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

    public void Stop()
    {
        if (_consumer == null)
        {
            return;
        }

        foreach (var consumerTag in _consumer.ConsumerTags) Channel!.BasicCancelNoWait(consumerTag);
    }

    public override void Dispose()
    {
        _receiver.Dispose();
        base.Dispose();
        _sender.Dispose();
    }

    public ValueTask RequeueAsync(RabbitMqEnvelope envelope)
    {
        if (!envelope.Acknowledged)
        {
            Channel!.BasicNack(envelope.DeliveryTag, false, false);
        }

        return _sender.SendAsync(envelope);
    }

    public void Complete(ulong deliveryTag)
    {
        Channel!.BasicAck(deliveryTag, true);
    }
}