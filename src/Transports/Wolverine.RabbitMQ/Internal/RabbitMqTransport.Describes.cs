using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Oakton.Descriptions;
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

        if (Queues.Any())
        {
            AnsiConsole.WriteLine();
            writeQueues();
        }

        if (Exchanges.Any())
        {
            AnsiConsole.WriteLine();
            writeExchanges();
        }
        

        return Task.CompletedTask;
    }

    private void writeExchanges()
    {
        var rule = new Rule("[blue]Exchanges[/]");
        rule.Alignment = Justify.Left;
        AnsiConsole.Write(rule);

        var table = new Table()
            .AddColumns(nameof(RabbitMqExchange.Name), "Type", nameof(RabbitMqExchange.AutoDelete),
                nameof(RabbitMqExchange.IsDurable), nameof(RabbitMqExchange.Arguments),
                nameof(RabbitMqExchange.Bindings));

        foreach (var exchange in Exchanges)
        {
            var arguments = exchange.Arguments.Any()
                ? exchange.Arguments.Select(pair => $"{pair.Key} = {pair.Value}").Join(", ")
                : "-";

            var bindings = "";
            if (exchange.Bindings().Any())
            {
                if (exchange.ExchangeType == ExchangeType.Topic)
                {
                    var groups = exchange.Bindings().GroupBy(x => x.Queue);
                    bindings = groups.Select(group =>
                    {
                        return $"Topics {group.Select(x => x.BindingKey).Join(", ")} to queue {group.Key.QueueName}";
                    }).Join(", ");
                }
                else
                {
                    bindings = $"To queue(s) {exchange.Bindings().Select(x => x.Queue.QueueName).Join(", ")}";
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
        rule.Alignment = Justify.Left;
        AnsiConsole.Write(rule);

        var table = new Table();
        table.AddColumns(nameof(RabbitMqQueue.QueueName), nameof(RabbitMqQueue.AutoDelete),
            nameof(RabbitMqQueue.IsDurable), nameof(RabbitMqQueue.IsExclusive), nameof(RabbitMqQueue.Arguments));

        foreach (var queue in Queues)
        {
            var arguments = queue.Arguments.Any()
                ? queue.Arguments.Select(pair => $"{pair.Key} = {pair.Value}").Join(", ")
                : "-";

            table.AddRow(queue.QueueName, queue.AutoDelete.ToString(), queue.IsDurable.ToString(),
                queue.IsExclusive.ToString(), arguments);
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