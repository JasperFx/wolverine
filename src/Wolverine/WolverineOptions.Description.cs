using JasperFx.Core;
using Oakton.Descriptions;
using Spectre.Console;

namespace Wolverine;

public partial class WolverineOptions : IDescribedSystemPart, IWriteToConsole
{
    async Task IDescribedSystemPart.Write(TextWriter writer)
    {
        foreach (var transport in Transports.Where(x => x.Endpoints().Any()))
        {
            await writer.WriteLineAsync(transport.Name);

            foreach (var endpoint in transport.Endpoints())
            {
                await writer.WriteLineAsync(
                    $"{endpoint.Uri}, Incoming: {endpoint.IsListener}, Reply Uri: {endpoint.IsUsedForReplies}");
            }

            await writer.WriteLineAsync();
        }
    }

    string IDescribedSystemPart.Title => "Wolverine Messaging Endpoints";

    Task IWriteToConsole.WriteToConsole()
    {
        var tree = new Tree("Transports and Endpoints");

        foreach (var transport in Transports.Where(x => x.Endpoints().Any()))
        {
            var transportNode = tree.AddNode($"[bold]{transport.Name}[/] [dim]({transport.Protocol}[/])");
            if (transport is ITreeDescriber d)
            {
                d.Describe(transportNode);
            }

            foreach (var endpoint in transport.Endpoints())
            {
                var endpointTitle = endpoint.Uri.ToString();
                if (endpoint.IsUsedForReplies || ReferenceEquals(endpoint, transport.ReplyEndpoint()))
                {
                    endpointTitle += " ([bold]Used for Replies[/])";
                }

                var endpointNode = transportNode.AddNode(endpointTitle);

                if (endpoint.IsListener)
                {
                    endpointNode.AddNode("[bold green]Listener[/]");
                }

                var props = endpoint.DescribeProperties();
                if (props.Any())
                {
                    var table = props.BuildTableForProperties();

                    endpointNode.AddNode(table);
                }

                if (endpoint.Subscriptions.Any())
                {
                    var subscriptions = endpointNode.AddNode("Subscriptions");
                    foreach (var subscription in endpoint.Subscriptions)
                        subscriptions.AddNode($"{subscription} ({subscription.ContentTypes.Join(", ")})");
                }
            }
        }

        AnsiConsole.Render(tree);

        return Task.CompletedTask;
    }
}