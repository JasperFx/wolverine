using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public partial class RabbitMqQueue : RabbitMqEndpoint, IBrokerQueue, IRabbitMqQueue
{
    private readonly RabbitMqTransport _parent;

    private bool _initialized;

    private ushort? _preFetchCount;

    internal RabbitMqQueue(string queueName, RabbitMqTransport parent, EndpointRole role = EndpointRole.Application) :
        base(new Uri($"{RabbitMqTransport.ProtocolName}://{QueueSegment}/{queueName}"), role, parent)
    {
        _parent = parent;
        QueueName = EndpointName = queueName;
        Mode = EndpointMode.Inline;

        if (QueueName != _parent.DeadLetterQueue.QueueName)
        {
            DeadLetterQueue = _parent.DeadLetterQueue;
        }
    }

    internal bool HasDeclared { get; private set; }

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
                    return (ushort)(ExecutionOptions.MaxDegreeOfParallelism * 2);
            }

            return 100;
        }
        set => _preFetchCount = value;
    }

    /// <summary>
    ///     Use to override the dead letter queue for this queue
    /// </summary>
    public DeadLetterQueue? DeadLetterQueue { get; set; }

    public override async ValueTask<bool> CheckAsync()
    {
        if (isSystemQueue())
        {
            return true;
        }

        try
        {
            using var channel = await _parent.CreateAdminChannelAsync();
            await channel.QueueDeclarePassiveAsync(QueueName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        // This is a reply uri owned by another node, so get out of here
        if (isSystemQueue() || AutoDelete)
        {
            return;
        }

        using var channel = await _parent.CreateAdminChannelAsync();
        foreach (var binding in _bindings)
        {
            logger.LogInformation("Removing binding {Key} from exchange {Exchange} to queue {Queue}",
                binding.BindingKey, binding.ExchangeName, binding.Queue);
            await binding.TeardownAsync(channel);
        }
        await channel.QueueDeleteAsync(QueueName, false, false, true);
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        if (isSystemQueue())
        {
            return;
        }

        using var channel = await _parent.CreateAdminChannelAsync();
        await DeclareAsync(channel, logger);
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        if (isSystemQueue())
        {
            return;
        }

        using var channel = await _parent.CreateAdminChannelAsync();
        try
        {
            await channel.QueuePurgeAsync(QueueName);
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
        using var channel = await _parent.CreateAdminChannelAsync();

        var result = await channel.QueueDeclarePassiveAsync(QueueName);

        var dict = new Dictionary<string, string>
            { { "name", QueueName }, { "count", result.MessageCount.ToString() } };

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
    public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();

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
        using var channel = await _parent.CreateAdminChannelAsync();

        var result = await channel.QueueDeclarePassiveAsync(QueueName);
        return result.MessageCount;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            using var channel = await _parent.CreateAdminChannelAsync();

            await InitializeAsync(channel, logger);
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
            if (_parent.AutoProvision)
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
        if (HasDeclared)
        {
            return;
        }

        if (DeadLetterQueue != null && DeadLetterQueue.Mode == DeadLetterQueueMode.Native)
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
                EndpointName, IsDurable, IsExclusive, AutoDelete);
            
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
                logger.LogDebug("Queue {Queue} exists with different configuration", QueueName);
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

        if (ListenerCount > 1)
        {
            var listeners = new List<RabbitMqListener>(ListenerCount);
            for (var i = 0; i < ListenerCount; i++)
            {
                var listener = new RabbitMqListener(runtime, this, _parent, receiver);
                await listener.CreateAsync();
                listeners.Add(listener);
            }

            return new ParallelListener(Uri, listeners);
        }

        var singleListener = new RabbitMqListener(runtime, this, _parent, receiver);
        await singleListener.CreateAsync();
        return singleListener;
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        var dlq = _parent.Queues[DeadLetterQueue?.QueueName ?? _parent.DeadLetterQueue.QueueName];
        deadLetterSender = dlq.CreateSender(runtime);
        return true;
    }
}