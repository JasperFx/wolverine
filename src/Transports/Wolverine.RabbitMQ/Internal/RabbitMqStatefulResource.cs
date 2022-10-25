using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Oakton.Resources;
using Spectre.Console;
using Spectre.Console.Rendering;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqStatefulResource : IStatefulResource
{
    private readonly RabbitMqTransport _transport;
    private readonly IWolverineRuntime _runtime;

    public RabbitMqStatefulResource(RabbitMqTransport transport, IWolverineRuntime runtime)
    {
        _transport = transport;
        _runtime = runtime;
    }

    public Task Check(CancellationToken token)
    {
        var queueNames = allKnownQueueNames();
        if (!queueNames.Any())
        {
            return Task.CompletedTask;
        }

        using var connection = _transport.BuildConnection();
        using var channel = connection.CreateModel();

        var missing = new List<string>();

        foreach (var queueName in queueNames)
        {
            try
            {
                channel.MessageCount(queueName);
            }
            catch (Exception)
            {
                missing.Add(queueName);
            }
        }

        channel.Close();
        connection.Close();

        if (missing.Any())
        {
            throw new Exception($"Missing known queues: {missing.Join(", ")}");
        }

        return Task.CompletedTask;
    }

    public Task ClearState(CancellationToken token)
    {
        PurgeAllQueues();
        return Task.CompletedTask;
    }

    public Task Teardown(CancellationToken token)
    {
        TeardownAll();
        return Task.CompletedTask;
    }

    public async Task Setup(CancellationToken token)
    {
        var consoleLogger = new ConsoleLogger();
        await _transport.ConnectAsync(_runtime);
        foreach (var endpoint in _transport.Endpoints())
        {
            await endpoint.InitializeAsync(consoleLogger);
        }
    }

    public Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        var queues = allKnownQueueNames();

        if (!queues.Any())
        {
            return Task.FromResult((IRenderable)new Markup("[gray]No known queues.[/]"));
        }

        var table = new Table
        {
            Alignment = Justify.Left
        };
        table.AddColumn("Queue");
        table.AddColumn("Count");

        using var connection = _transport.BuildConnection();
        using var channel = connection.CreateModel();

        foreach (var queue in queues)
        {
            try
            {
                var count = channel.MessageCount(queue);
                table.AddRow(queue, count.ToString());
            }
            catch (Exception)
            {
                table.AddRow(new Markup(queue), new Markup("[red]Does not exist[/]"));
            }
        }

        return Task.FromResult((IRenderable)table);
    }

    string IStatefulResource.Type => TransportConstants.WolverineTransport;
    public string Name => "Rabbit MQ Transport";

    internal void PurgeAllQueues()
    {
        using var connection = _transport.BuildConnection();
        using var channel = connection.CreateModel();

        try
        {
            foreach (var queue in _transport.Queues)
            {
                Console.WriteLine($"Purging Rabbit MQ queue '{queue}'");
                queue.Purge(channel);
            }
        }
        finally
        {
            channel.Close();
            connection.Close();
        }
    }

    internal void TeardownAll()
    {
        using var connection = _transport.BuildConnection();
        using var channel = connection.CreateModel();

        foreach (var exchange in _transport.Exchanges)
        {
            Console.WriteLine($"Tearing down Rabbit MQ exchange {exchange}");
            exchange.Teardown(channel);
        }

        foreach (var queue in _transport.Queues)
        {
            Console.WriteLine($"Tearing down Rabbit MQ queue {queue}");
            queue.Teardown(channel);
        }

        channel.Close();

        connection.Close();
    }
        
    private string[] allKnownQueueNames()
    {
        return _transport.Queues.Select(x => x.QueueName).ToArray()!;
    }
}