using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using Spectre.Console;

namespace Wolverine.RabbitMQ.Internal;

public partial class RabbitMqTransport : IDescribedSystemPart, IWriteToConsole
{
    Task IDescribedSystemPart.Write(TextWriter writer)
    {
        return writer.WriteLineAsync("Use the Console output.");
    }

    string IDescribedSystemPart.Title => "Wolverine Rabbit MQ Usage";

    Task IWriteToConsole.WriteToConsole()
    {
        writeBasics();

        if (Queues.Count != 0)
        {
            AnsiConsole.WriteLine();
            writeQueues();
        }

        if (Exchanges.Count != 0)
        {
            AnsiConsole.WriteLine();
            writeExchanges();
        }

        return Task.CompletedTask;
    }

    private void writeExchanges()
    {
        var rule = new Rule("[blue]Exchanges[/]");
        rule.Justify(Justify.Left);
        AnsiConsole.Write(rule);

        var table = new Table()
            .AddColumns(nameof(RabbitMqExchange.Name), "Type", nameof(RabbitMqExchange.AutoDelete),
                nameof(RabbitMqExchange.IsDurable), nameof(RabbitMqExchange.Arguments), nameof(RabbitMqQueue.Bindings));

        var queueBindings = Queues.SelectMany(x => x.Bindings()).ToArray();
        
        foreach (var exchange in Exchanges)
        {
            var arguments = exchange.Arguments.Any()
                ? exchange.Arguments.Select(pair => $"{pair.Key} = {pair.Value}").Join(", ")
                : "-";
            
            var bindings = "";
            var exchangeBindings = queueBindings.Where(x => x.ExchangeName == exchange.Name).ToArray();
            if (exchangeBindings.Any())
            {
                if (exchange.ExchangeType == ExchangeType.Topic)
                {
                    var groups = exchangeBindings.GroupBy(x => x.Queue);
                    bindings = groups.Select(group =>
                    {
                        return $"Topics {group.Select(x => x.BindingKey).Join(", ")} to queue {group.Key.QueueName}";
                    }).Join(", ");
                }
                else
                {
                    bindings = $"To queue(s) {exchangeBindings.Select(x => x.Queue.QueueName).Join(", ")}";
                }
            }

            table.AddRow(exchange.Name, exchange.ExchangeType.ToString(), exchange.AutoDelete.ToString(), exchange.IsDurable.ToString(), arguments,
                bindings);
        }

        AnsiConsole.Write(table);
    }

    private void writeQueues()
    {
        var rule = new Rule("[blue]Queues[/]");
        rule.Justify(Justify.Left);
        AnsiConsole.Write(rule);

        var table = new Table();
        table.AddColumns(nameof(RabbitMqQueue.QueueName), nameof(RabbitMqQueue.AutoDelete),
            nameof(RabbitMqQueue.IsDurable), nameof(RabbitMqQueue.IsExclusive), nameof(RabbitMqQueue.Arguments), nameof(RabbitMqQueue.Bindings));

        foreach (var queue in Queues)
        {
            var arguments = queue.Arguments.Any()
                ? queue.Arguments.Select(pair => $"{pair.Key} = {pair.Value}").Join(", ")
                : "-";

            var bindings = string.Empty;

            if (queue.Bindings().Any())
            {
               bindings = $"From exchange(s) {queue.Bindings().Select(x => x.ExchangeName).Join(", ")}";
            }

            table.AddRow(queue.QueueName, queue.AutoDelete.ToString(), queue.IsDurable.ToString(),
                queue.IsExclusive.ToString(), arguments, bindings);
        }

        AnsiConsole.Write(table);
    }

    private void writeBasics()
    {
        var grid = new Grid()
            .AddColumn()
            .AddColumn()
            .AddRow(nameof(ConnectionFactory.HostName), ConnectionFactory.HostName)
            .AddRow(nameof(ConnectionFactory.Port), ConnectionFactory.Port.ToString());

        if (ConnectionFactory.VirtualHost.IsNotEmpty())
        {
            grid.AddRow(nameof(ConnectionFactory.VirtualHost), ConnectionFactory.VirtualHost);
        }

        grid
            .AddRow(nameof(AutoProvision), AutoProvision.ToString())
            .AddRow(nameof(AutoPurgeAllQueues), AutoPurgeAllQueues.ToString());

        AnsiConsole.Write(grid);
    }
}