using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public enum QueueType
{
    /// <summary>
    /// "Classic" mode. See https://www.rabbitmq.com/docs/classic-queues
    /// </summary>
    classic,
    
    /// <summary>
    /// Declares this queue in Rabbit MQ as a quorum queue. See https://www.rabbitmq.com/docs/quorum-queues
    /// </summary>
    quorum,
    
    /// <summary>
    /// Declare this queue as a Rabbit MQ stream. See https://www.rabbitmq.com/docs/streams
    /// </summary>
    stream
}

public partial class RabbitMqQueue : RabbitMqEndpoint, IBrokerQueue, IRabbitMqQueue
{
    private readonly RabbitMqTransport _parent;

    private bool _initialized;

    private ushort? _preFetchCount;

    internal RabbitMqQueue(string queueName, RabbitMqTransport parent, EndpointRole role = EndpointRole.Application) :
        base(new Uri($"{parent.Protocol}://{QueueSegment}/{queueName}"), role, parent)
    {
        _parent = parent;
        QueueName = EndpointName = queueName;
        Mode = EndpointMode.Inline;
        BrokerRole = "queue";

        if (Role == EndpointRole.Application && QueueName != _parent.DeadLetterQueue.QueueName)
        {
            DeadLetterQueue = _parent.DeadLetterQueue.Clone();
        }
    }

    
    /// <summary>
    /// Governs the declaration of the Rabbit MQ queue if Wolverine is building the queues
    /// Has no impact on Wolverine or your code. Default is classic
    /// </summary>
    public QueueType QueueType { get; set; } = QueueType.classic;

    internal bool HasDeclared { get; private set; }

    /// <summary>
    /// For durable (inbox-backed) listeners, the maximum number of prefetched deliveries the
    /// consumer will coalesce into one batched inbox insert (with a 5ms max accumulation age).
    /// 1 reverts to strict message-at-a-time persistence. Ignored for Buffered/Inline
    /// endpoints. Default 100. See GH-3492.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 100;

    /// <summary>
    /// Overrides the transport-wide consumer dispatch concurrency for just this queue's listening
    /// channels. This is the RabbitMQ client's own limit on how many deliveries it hands to a
    /// consumer at once, and with the default of 1 an Inline listener consumes strictly one
    /// message at a time no matter what MaxDegreeOfParallelism says. Null uses the transport-wide
    /// value set through ConfigureChannelCreation(). See GH-3492.
    /// </summary>
    public ushort? ConsumerDispatchConcurrency { get; set; }

    /// <summary>
    /// GH-3708. RabbitMQ is the first transport to accept <see cref="EndpointMode.NativeAck"/>. It qualifies on
    /// both counts the mode requires: deliveries are settled individually -- GH-3706 made every ack
    /// <c>multiple: false</c>, which matters because under <c>multiple: true</c> an out-of-order completion
    /// would silently ack every lower delivery tag still in flight -- and GH-3687's <c>DeliveredOn</c> /
    /// <c>CanSettle</c> plumbing already makes a completion-time ack safe when it arrives from an arbitrary
    /// worker thread rather than the consumer callback.
    ///
    /// <para>
    /// Only queues, not exchanges or topics: native acks are a listening concept, and RabbitMQ listens on queues.
    /// </para>
    /// </summary>
    protected override bool supportsNativeAck => true;

    /// <summary>
    ///     The number of unacknowledged messages that can be processed concurrently
    /// </summary>
    public ushort PreFetchCount
    {
        get
        {
            if (_preFetchCount.HasValue)
            {
                return _preFetchCount.Value;
            }

            switch (Mode)
            {
                case EndpointMode.BufferedInMemory:
                case EndpointMode.Durable:
                    return (ushort)(MaxDegreeOfParallelism * 2);

                case EndpointMode.NativeAck:
                    // GH-3708. Prefetch IS the back pressure for this mode -- nothing is acked until the handler
                    // succeeds, so the unacked window is what bounds the in-memory execution block. It has to cover
                    // every lane that can be busy at once, which is the partition slot count when the endpoint is
                    // group-partitioned and MaxDegreeOfParallelism otherwise, doubled so a lane is never starved
                    // waiting on the next delivery.
                    var lanes = GroupShardingSlotNumber.HasValue
                        ? Math.Max((int)GroupShardingSlotNumber.Value, MaxDegreeOfParallelism)
                        : MaxDegreeOfParallelism;
                    return (ushort)(lanes * 2);
            }

            return 100;
        }
        set => _preFetchCount = value;
    }

    /// <summary>
    /// When true, listener shutdown waits for prefetched messages in the RabbitMQ client's dispatch
    /// buffer to reach the consumer before closing the channel, preventing silent redeliveries of
    /// messages that were prefetched but not yet handled. Default is false.
    ///
    /// Only a hard guarantee at <c>ConsumerDispatchConcurrency</c> of 1 (the default). At higher
    /// concurrency the client handles deliveries and cancel-ok in parallel, so it degrades to
    /// best-effort -- some messages may still be redelivered, but fewer than without waiting.
    /// </summary>
    public bool DrainWaitForPrefetch { get; set; }

    /// <summary>
    ///     Use to override the dead letter queue for this queue
    /// </summary>
    public DeadLetterQueue? DeadLetterQueue { get; set; }

    /// <summary>
    ///     The unique id for listener that is actively listening to this queue.
    /// </summary>
    public string? CustomListenerId { get; set; }

    public override async ValueTask<bool> CheckAsync()
    {
        if (isSystemQueue())
        {
            return true;
        }

        try
        {
            await _parent.WithAdminChannelAsync(channel => channel.QueueDeclarePassiveAsync(QueueName));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        // This is a reply uri owned by another node, so get out of here. Externally-owned queues
        // belong to another system and must not be deleted (nor their bindings torn down). GH-3064.
        if (isSystemQueue() || AutoDelete || IsExternallyOwned)
        {
            return;
        }

        await _parent.WithAdminChannelAsync(async channel =>
        {
            foreach (var binding in _bindings)
            {
                logger.LogInformation("Removing binding {Key} from exchange {Exchange} to queue {Queue}",
                    binding.BindingKey, binding.ExchangeName, binding.Queue);
                await binding.TeardownAsync(channel);
            }

            await channel.QueueDeleteAsync(QueueName, false, false, true);
        });
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        // Externally-owned queues are declared/managed by another system; don't try to create them. GH-3064.
        if (isSystemQueue() || IsExternallyOwned)
        {
            return;
        }

        await _parent.WithAdminChannelAsync(channel => DeclareAsync(channel, logger));
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        // It's invalid to purge a stream
        if (isSystemQueue() || QueueType == QueueType.stream)
        {
            return;
        }
        
        try
        {
            await _parent.WithAdminChannelAsync(channel => channel.QueuePurgeAsync(QueueName));
        }
        catch (Exception e)
        {
            if (e.Message.Contains("NOT_FOUND - no queue"))
            {
                return;
            }

            throw;
        }

        return;
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        long messageCount = 0;
        await _parent.WithAdminChannelAsync(async channel =>
        {
            var result = await channel.QueueDeclarePassiveAsync(QueueName);
            messageCount += result.MessageCount;
        });

        var dict = new Dictionary<string, string>
            { { "name", QueueName }, { "count", messageCount.ToString() } };

        return dict;
    }

    public string QueueName { get; }

    /// <summary>
    ///     If true, this queue will be deleted when the connection is closed. This is mostly useful
    ///     for temporary, response queues
    /// </summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    ///     If true, this queue can only be used by a single connection
    /// </summary>
    public bool IsExclusive { get; set; }

    /// <summary>
    ///     The default is true. Governs whether queue messages
    /// </summary>
    public bool IsDurable { get; set; } = true;

    /// <summary>
    ///     Arguments for Rabbit MQ queue declarations. See the Rabbit MQ .NET client documentation at
    ///     https://www.rabbitmq.com/dotnet.html
    /// </summary>
    [IgnoreDescription]
    public IDictionary<string, object?> Arguments { get; } = new Dictionary<string, object?>();

    /// <summary>
    ///     Arguments for Rabbit MQ channel consume operations
    /// </summary>
    [IgnoreDescription]
    public IDictionary<string, object?> ConsumerArguments { get; } = new Dictionary<string, object?>();

    /// <summary>
    ///     Create a "time to live" limit for messages in this queue. Sets the Rabbit MQ x-message-ttl argument on a queue
    /// </summary>
    /// <param name="limit"></param>
    public void TimeToLive(TimeSpan limit)
    {
        Arguments["x-message-ttl"] = Convert.ToInt32(limit.TotalMilliseconds);
    }

    /// <summary>
    ///     Declare that Wolverine should purge the existing queue
    ///     of all existing messages on startup
    /// </summary>
    public bool PurgeOnStartup { get; set; }

    /// <summary>
    ///     Mostly for testing
    /// </summary>
    /// <returns></returns>
    public async Task<long> QueuedCountAsync()
    {
        long messageCount = 0;
        await _parent.WithAdminChannelAsync(async channel =>
        {
            var result = await channel.QueueDeclarePassiveAsync(QueueName);
            messageCount += result.MessageCount;
        });

        return messageCount;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            await _parent.WithAdminChannelAsync(channel => InitializeAsync(channel, logger).AsTask());
        }
        finally
        {
            _initialized = true;
        }
    }

    internal async ValueTask InitializeAsync(IChannel channel, ILogger logger)
    {
        // This is a reply uri owned by another node, so get out of here
        if (isSystemQueue())
        {
            return;
        }

        if (_parent.AutoProvision || _parent.AutoPurgeAllQueues || PurgeOnStartup)
        {
            // Externally-owned queues (and their bindings) are managed by another system; skip the
            // declare even when AutoProvision is on so startup doesn't fail without configure ACLs. GH-3064.
            if (_parent.AutoProvision && !IsExternallyOwned)
            {
                await DeclareAsync(channel, logger);
            }

            if (!IsDurable || IsExclusive || AutoDelete)
            {
                return;
            }

            if (PurgeOnStartup || _parent.AutoPurgeAllQueues)
            {
                await channel.QueuePurgeAsync(QueueName);
            }
        }

        return;
    }

    private bool isSystemQueue()
    {
        return QueueName.StartsWith("wolverine.") && Role == EndpointRole.Application;
    }

    internal override string RoutingKey()
    {
        return QueueName;
    }

    internal async Task DeclareAsync(IChannel channel, ILogger logger)
    {
        if (QueueType != QueueType.classic)
        {
            Arguments[RabbitMqTransport.QueueTypeHeader] = QueueType.ToString();
        }
        
        if (DeadLetterQueue is { Mode: DeadLetterQueueMode.Native } && QueueType != QueueType.stream)
        {
            Arguments[RabbitMqTransport.DeadLetterQueueHeader] = DeadLetterQueue.ExchangeName;
        }
        else
        {
            Arguments.Remove(RabbitMqTransport.DeadLetterQueueHeader);
        }

        try
        {
            await channel.QueueDeclareAsync(QueueName, IsDurable, IsExclusive, AutoDelete, Arguments);
            logger.LogInformation(
                "Declared Rabbit MQ queue '{Name}' as IsDurable={IsDurable}, IsExclusive={IsExclusive}, AutoDelete={AutoDelete}",
                QueueName, IsDurable, IsExclusive, AutoDelete);
            
            if (_bindings.Count > 0)
            {
                foreach (var binding in _bindings)
                {
                    await binding.DeclareAsync(channel, logger);
                }
            }
        }
        catch (OperationInterruptedException e)
        {
            if (e.Message.Contains("inequivalent arg"))
            {
                // Rabbit MQ answers a mismatched declaration with a channel level 406, so this channel
                // is closed and unusable even though Wolverine is choosing to tolerate the mismatch.
                // Whatever the caller does with it next -- BasicQosAsync, for a listener -- throws an
                // ObjectDisposedException that says nothing about what the broker actually objected to.
                // Log the broker's own complaint here at a level someone will see. See GH-3871.
                logger.LogWarning(e,
                    "Rabbit MQ rejected the declaration of queue '{Queue}' because it already exists with a different configuration. Wolverine will use the existing queue, but the broker has closed this channel.",
                    QueueName);
                return;
            }

            throw;
        }

        HasDeclared = true;
    }

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();

        dict.Add(nameof(QueueName), QueueName);

        if (DeadLetterQueue != null)
        {
            dict.Add("Dead Letter Queue", DeadLetterQueue.QueueName);
        }

        if (ListenerCount > 0 && IsListener)
        {
            dict.Add(nameof(ListenerCount), ListenerCount);
        }

        return dict;
    }

    public override string ToString()
    {
        return $"RabbitMqQueue: {QueueName}";
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        await InitializeAsync(runtime.LoggerFactory.CreateLogger<RabbitMqQueue>());

        return await _parent.BuildListenerAsync(runtime, receiver, this);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (DeadLetterQueue is { Mode: DeadLetterQueueMode.Native })
        {
            var dlq = _parent.Queues[DeadLetterQueue?.QueueName ?? _parent.DeadLetterQueue.QueueName];
            deadLetterSender = dlq.CreateSender(runtime);
            return true;
        }

        deadLetterSender = default;
        return false;
    }

    // Native and InteropFriendly modes route to a native RabbitMQ dead letter queue; WolverineStorage
    // (and no DLQ) uses Wolverine's durable storage. EnableDeadLetterQueueRecovery() bridges the
    // native queue back into durable storage.
    public override DeadLetterStorageMode DeadLetterStorage
    {
        get
        {
            if (DeadLetterQueue is null || DeadLetterQueue.Mode == DeadLetterQueueMode.WolverineStorage)
            {
                return DeadLetterStorageMode.Durable;
            }

            return _parent.EnableDeadLetterQueueRecovery
                ? DeadLetterStorageMode.NativeWithRecovery
                : DeadLetterStorageMode.Native;
        }
    }
}