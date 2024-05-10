using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;

namespace Wolverine.RabbitMQ;

public static class DynamicObjectCreationExtensions
{
    /// <summary>
    /// Runtime creation of Rabbit MQ queues, exchanges, and bindings.
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="creation"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public static void ModifyRabbitMqObjects(this IWolverineRuntime runtime, Action<RabbitMqObjects> creation)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (creation == null)
        {
            throw new ArgumentNullException(nameof(creation));
        }

        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var objects = new RabbitMqObjects(transport, runtime.LoggerFactory.CreateLogger<RabbitMqObjects>());

        creation(objects);

        objects.DeclareAll();
    }

    /// <summary>
    /// Will un-bind a queue to an exchange
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="exchangeName"></param>
    /// <param name="routingKey">Binding key name</param>
    public static void UnBindRabbitMqQueue(this IWolverineRuntime runtime, string queueName, string exchangeName, string routingKey)
    {
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        using var model = transport.ListeningConnection.CreateModel();

        model.QueueUnbind(queueName, exchangeName, routingKey);

        model.Close();
    }
}

public class RabbitMqObjects
{
    private readonly RabbitMqTransport _transport;
    private readonly ILogger<RabbitMqObjects> _logger;

    private readonly List<RabbitMqExchange> _exchanges = new();
    private readonly List<RabbitMqQueue> _queues = new();

    internal RabbitMqObjects(RabbitMqTransport transport, ILogger<RabbitMqObjects> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Declare a new exchange. Will not be created if it already exists
    /// on the Rabbit MQ broker. Will throw an exception if the declared configuration
    /// is different from the existing exchange
    /// </summary>
    /// <param name="exchangeName"></param>
    /// <returns></returns>
    public RabbitMqExchange DeclareExchange(string exchangeName)
    {
        var exchange = _transport.Exchanges[exchangeName];
        _exchanges.Add(exchange);
        return exchange;
    }

    /// <summary>
    /// Declare a new queue. Will not be created it it already exists on
    /// the Rabbit MQ broker. Will throw an exception if the declared configuration
    /// is different from the existing queue
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    public RabbitMqQueue DeclareQueue(string queueName)
    {
        var queue = _transport.Queues[queueName];
        _queues.Add(queue);

        return queue;
    }

    internal void DeclareAll()
    {
        using var model = _transport.ListeningConnection.CreateModel();

        foreach (var exchange in _exchanges)
        {
            exchange.Declare(model, _logger);

            foreach (var binding in exchange.Bindings())
            {
                binding.Declare(model, _logger);
            }
        }

        foreach (var queue in _queues)
        {
            queue.Declare(model, _logger);
        }

        model.Close();
    }
}