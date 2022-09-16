using System.Collections.Generic;
using System.Linq;
using Baseline;
using Oakton.Descriptions;
using Spectre.Console;

namespace Wolverine.RabbitMQ.Internal
{
    public partial class RabbitMqTransport : ITreeDescriber
    {
        void ITreeDescriber.Describe(TreeNode parentNode)
        {
            var props = new Dictionary<string, object>
            {
                { "HostName", ConnectionFactory.HostName },
                { "Port", ConnectionFactory.Port == -1 ? 5672 : ConnectionFactory.Port },
                { nameof(AutoProvision), AutoProvision },
                { nameof(AutoPurgeAllQueues), AutoPurgeAllQueues }
            };

            var table = props.BuildTableForProperties();
            parentNode.AddNode(table);


            if (Exchanges.Any())
            {
                var exchangesNode = parentNode.AddNode("Exchanges");
                foreach (var exchange in Exchanges)
                {
                    var exchangeNode = exchangesNode.AddNode(exchange.Name);
                    if (exchange.Bindings().Any())
                    {
                        var bindings = exchangeNode.AddNode("Bindings");

                        var bindingTable = new Table();
                        bindingTable.AddColumn("Key");
                        bindingTable.AddColumn("Queue Name");
                        bindingTable.AddColumn("Arguments");

                        foreach (var binding in exchange.Bindings())
                        {
                            bindingTable.AddRow(binding.BindingKey ?? string.Empty, binding.Queue.Name ?? string.Empty,
                                binding.Arguments.Select(pair => $"{pair.Key}={pair.Value}").Join(", "));
                        }

                        bindings.AddNode(bindingTable);
                    }
                }
            }

            var queueNode = parentNode.AddNode("Queues");
            foreach (var queue in Queues) queueNode.AddNode(queue.Name);
        }
    }
}
