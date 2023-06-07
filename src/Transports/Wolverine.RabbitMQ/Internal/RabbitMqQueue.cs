using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqQueue : RabbitMqEndpoint, IBrokerQueue, IRabbitMqQueue
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
        DeadLetterQueue = _parent.DeadLetterQueue;
    }

    internal bool HasDeclared { get; private set; }

    /// <summary>
    ///     Limit on the combined size of pre-fetched messages. The default in Wolverine is 0, which
    ///     denotes an unlimited size.
    /// </summary>
    public uint PreFetchSize { get; set; }

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

    public override ValueTask<bool> CheckAsync()
    {
        if (isSystemQueue())
        {
            return ValueTask.FromResult(true);
        }

        try
        {
            using var channel = _parent.ListeningConnection.CreateModel();
            channel.QueueDeclarePassive(QueueName);
            return ValueTask.FromResult(true);
        }
        catch (Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        // This is a reply uri owned by another node, so get out of here
        if (isSystemQueue() || AutoDelete)
        {
            return ValueTask.CompletedTask;
        }

        using var channel = _parent.ListeningConnection.CreateModel();
        channel.QueueDeleteNoWait(QueueName);

        return ValueTask.CompletedTask;
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        if (isSystemQueue())
        {
            return ValueTask.CompletedTask;
        }

        using var channel = _parent.ListeningConnection.CreateModel();
        Declare(channel, logger);

        return ValueTask.CompletedTask;
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        if (isSystemQueue())
        {
            return ValueTask.CompletedTask;
        }

        using var channel = _parent.ListeningConnection.CreateModel();
        try
        {
            channel.QueuePurge(QueueName);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("NOT_FOUND - no queue")) return ValueTask.CompletedTask;

            throw;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        using var channel = _parent.ListeningConnection.CreateModel();

        var result = channel.QueueDeclarePassive(QueueName);

        var dict = new Dictionary<string, string>
            { { "name", QueueName }, { "count", result.MessageCount.ToString() } };

        return ValueTask.FromResult(dict);
    }

    /// <summary>
    /// Mostly for testing
    /// </summary>
    /// <returns></returns>
    public long QueuedCount()
    {
        using var channel = _parent.ListeningConnection.CreateModel();

        var result = channel.QueueDeclarePassive(QueueName);
        return result.MessageCount;
    }

    public string QueueName { get; }
    
    /// <summary>
    /// Use to override the dead letter queue for this queue
    /// </summary>
    public DeadLetterQueue? DeadLetterQueue { get; set; }

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

    public override ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var connection = _parent.ListeningConnection;

            return InitializeAsync(connection, logger);
        }
        finally
        {
            _initialized = true;
        }
    }

    internal ValueTask InitializeAsync(IConnection connection, ILogger logger)
    {
        // This is a reply uri owned by another node, so get out of here
        if (isSystemQueue())
        {
            return ValueTask.CompletedTask;
        }

        if (_parent.AutoProvision || _parent.AutoPurgeAllQueues || PurgeOnStartup)
        {
            using var channel = connection.CreateModel();
            if (_parent.AutoProvision)
            {
                Declare(channel, logger);
            }

            if (!IsDurable || IsExclusive || AutoDelete)
            {
                return ValueTask.CompletedTask;
            }

            if (PurgeOnStartup || _parent.AutoPurgeAllQueues)
            {
                channel.QueuePurge(QueueName);
            }
        }

        return ValueTask.CompletedTask;
    }

    private bool isSystemQueue()
    {
        return QueueName.StartsWith("wolverine.") && Role == EndpointRole.Application;
    }

    internal override string RoutingKey()
    {
        return QueueName;
    }

    internal void Declare(IModel channel, ILogger logger)
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
            channel.QueueDeclare(QueueName, IsDurable, IsExclusive, AutoDelete, Arguments);
            logger.LogInformation(
                "Declared Rabbit MQ queue '{Name}' as IsDurable={IsDurable}, IsExclusive={IsExclusive}, AutoDelete={AutoDelete}",
                EndpointName, IsDurable, IsExclusive, AutoDelete);
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

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        await InitializeAsync(runtime.LoggerFactory.CreateLogger<RabbitMqQueue>());

        if (ListenerCount > 1)
        {
            var listeners = new List<RabbitMqListener>();
            for (int i = 0; i < ListenerCount; i++)
            {
                var listener = new RabbitMqListener(runtime, this, _parent, receiver);
                listeners.Add(listener);
            }

            return new ParallelListener(Uri, listeners);
        }

        return new RabbitMqListener(runtime, this, _parent, receiver);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        var dlq = _parent.Queues[DeadLetterQueue?.QueueName ?? _parent.DeadLetterQueue.QueueName];
        deadLetterSender = dlq.CreateSender(runtime);
        return true;
    }
}