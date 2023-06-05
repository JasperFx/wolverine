using Microsoft.Extensions.Logging;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;

namespace Wolverine.RabbitMQ;

public static class DynamicObjectCreationExtensions
{
    
    public static void CreateRabbitMqObjects(this IWolverineRuntime runtime, Action<RabbitMqObjects> creation)
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

    public RabbitMqExchange DeclareExchange(string exchangeName)
    {
        var exchange = _transport.Exchanges[exchangeName];
        _exchanges.Add(exchange);
        return exchange;
    }

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