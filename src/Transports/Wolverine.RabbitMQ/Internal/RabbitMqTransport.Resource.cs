using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using Oakton.Resources;
using RabbitMQ.Client.Exceptions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Wolverine.RabbitMQ.Internal
{
    public partial class RabbitMqTransport : IStatefulResource
    {
        public Task Check(CancellationToken token)
        {
            var queueNames = allKnownQueueNames();
            if (!queueNames.Any())
            {
                return Task.CompletedTask;
            }

            using var connection = BuildConnection();
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

        public Task Setup(CancellationToken token)
        {
            InitializeAllObjects(new ConsoleLogger());
            return Task.CompletedTask;
        }

        public Task<IRenderable> DetermineStatus(CancellationToken token)
        {
            var queues = allKnownQueueNames();

            if (!queues.Any())
            {
                return Task.FromResult((IRenderable)new Markup("[gray]No known queues.[/]"));
            }

            var table = new Table();
            table.Alignment = Justify.Left;
            table.AddColumn("Queue");
            table.AddColumn("Count");

            using var connection = BuildConnection();
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

        string IStatefulResource.Type => "WolverineTransport";

        internal void PurgeAllQueues()
        {
            using var connection = BuildConnection();
            using var channel = connection.CreateModel();

            foreach (var queue in Queues)
            {
                Console.WriteLine($"Purging Rabbit MQ queue '{queue}'");
                queue.Purge(channel);
            }

            var others = _endpoints.Select(x => x.QueueName).Where(x => x.IsNotEmpty())
                .Where(x => Queues.All(q => q.Name != x)).ToArray();

            foreach (var other in others)
            {
                var queue = Queues[other];
                if (queue.AutoDelete) continue;
                
                Console.WriteLine($"Purging Rabbit MQ queue '{other}'");
                try
                {
                    channel.QueuePurge(other);
                }
                catch (OperationInterruptedException e)
                {
                    if (!e.Message.Contains("NOT_FOUND"))
                    {
                        throw;
                    }
                }
            }

            channel.Close();

            connection.Close();
        }

        internal void TeardownAll()
        {
            using var connection = BuildConnection();
            using var channel = connection.CreateModel();

            foreach (var exchange in Exchanges)
            {
                Console.WriteLine($"Tearing down Rabbit MQ exchange {exchange}");
                exchange.Teardown(channel);
            }

            foreach (var queue in Queues)
            {
                Console.WriteLine($"Tearing down Rabbit MQ queue {queue}");
                queue.Teardown(channel);
            }

            channel.Close();

            connection.Close();
        }

        internal void InitializeAllObjects(ILogger logger)
        {
            using var connection = BuildConnection();
            using var channel = connection.CreateModel();

            foreach (var queue in Queues.Where(x => !x.AutoDelete))
            {
                logger.LogInformation("Declaring Rabbit MQ queue {Queue}", queue);
                queue.Declare(channel, logger);
            }

            foreach (var exchange in Exchanges)
            {
                logger.LogInformation("Declaring Rabbit MQ exchange {Exchange}", exchange);
                exchange.Declare(channel, logger);
            }

            channel.Close();

            connection.Close();
        }

        private string[] allKnownQueueNames()
        {
            return Queues.Select(x => x.Name).ToArray()!;
        }
    }

    internal class ConsoleLogger : ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return new Disposable();
        }

        internal class Disposable : IDisposable
        {
            public void Dispose()
            {

            }
        }
    }
}
