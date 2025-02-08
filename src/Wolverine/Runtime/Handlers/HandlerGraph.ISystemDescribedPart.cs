using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Spectre.Console;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph : IDescribedSystemPart, IWriteToConsole
{
    async Task IDescribedSystemPart.Write(TextWriter writer)
    {
        foreach (var chain in Chains.OrderBy(x => x.MessageType.Name))
        {
            await writer.WriteLineAsync($"Message: {chain.MessageType.FullNameInCode()}");
            foreach (var handler in chain.Handlers)
            {
                await writer.WriteLineAsync(
                    $"Handled by {handler.HandlerType.FullNameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ")})");
            }
        }
    }

    string IDescribedSystemPart.Title => "Wolverine Handlers";

    Task IWriteToConsole.WriteToConsole()
    {
        writeHandlerDiscoveryRules();

        if (Chains.Length == 0)
        {
            AnsiConsole.Write(
                "[yellow]No message handlers were discovered, you may want to review the discovery rules above.[/]");
        }
        else
        {
            writeHandlerTable();
        }

        return Task.CompletedTask;
    }

    private void writeHandlerTable()
    {
        var hasStickys = Chains.Any(x => x.ByEndpoint.Any());
        
        var table = new Table();
        table.AddColumn("Message Name");
        table.AddColumn("[bold]Message Type[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
        table.AddColumn("[bold]Handler.Method()[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
        table.AddColumn("Generated Type Name");

        if (hasStickys)
        {
            table.AddColumn("Endpoints");
        }

        foreach (var chain in Chains)
        {
            var messageType = $"[bold]{chain.MessageType.NameInCode()}[/]\n  [dim]{chain.MessageType.Namespace}[/]";
            
            if (hasStickys)
            {
                // Default handlers
                if (chain.Handlers.Any())
                {
                    var handlerType = chain.Handlers.Select(handler =>
                            $"[bold]{handler.HandlerType.NameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ")})[/]\n  [dim]{handler.HandlerType.Namespace}[/]")
                        .Join("\n");

                    table.AddRow(chain.MessageType.ToMessageTypeName(), messageType, handlerType, chain.TypeName, "");
                }

                foreach (var handlerChain in chain.ByEndpoint)
                {
                    var handlerType = handlerChain.Handlers.Select(handler =>
                            $"[bold]{handler.HandlerType.NameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ")})[/]\n  [dim]{handler.HandlerType.Namespace}[/]")
                        .Join("\n");

                    var endpoints = handlerChain.Endpoints.Select(x => x.Uri.ToString()).Join(", ");
                    table.AddRow(chain.MessageType.ToMessageTypeName(), messageType, handlerType, handlerChain.TypeName, endpoints);
                }
            }
            else
            {
                var handlerType = chain.Handlers.Select(handler =>
                        $"[bold]{handler.HandlerType.NameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ")})[/]\n  [dim]{handler.HandlerType.Namespace}[/]")
                    .Join("\n");

                table.AddRow(chain.MessageType.ToMessageTypeName(), messageType, handlerType, chain.TypeName);
            }
        }

        AnsiConsole.Render(table);
    }

    private void writeHandlerDiscoveryRules()
    {
        var tree = new Tree("Handler Discovery Rules");
        var assemblies = tree.AddNode("Assemblies");
        foreach (var assembly in Discovery.Assemblies) assemblies.AddNode(assembly.GetName().Name.EscapeMarkup());

        var typeRules = tree.AddNode("Handler Type Rules");
        var includedNode = typeRules.AddNode("Include:");
        foreach (var filter in Discovery.HandlerQuery.Includes) includedNode.AddNode(filter.Description.EscapeMarkup());

        var excludedNode = typeRules.AddNode("Exclude:");
        foreach (var exclude in Discovery.HandlerQuery.Excludes)
            excludedNode.AddNode(exclude.Description.EscapeMarkup());

        var methodRules = tree.AddNode("Handler Method Rules");
        var includedMethods = methodRules.AddNode("Include:");
        foreach (var include in Discovery.MethodIncludes) includedMethods.AddNode(include.Description.EscapeMarkup());

        var excludedMethods = methodRules.AddNode("Exclude:");
        foreach (var filter in Discovery.MethodExcludes) excludedMethods.AddNode(filter.Description.EscapeMarkup());

        AnsiConsole.Write(tree);

        AnsiConsole.WriteLine();
    }
}