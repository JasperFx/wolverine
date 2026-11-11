using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Spectre.Console;
using Wolverine.Util;

namespace Wolverine.Runtime.Handlers;

public partial class HandlerGraph 
{
    internal Task WriteToConsole()
    {
        writeHandlerDiscoveryRules();

        if (Chains.Length == 0)
        {
            AnsiConsole.Write(
                "[yellow]No message handlers were discovered, you may want to review the discovery rules above.[/]");
        }
        else
        {
            var hasStickys = Chains.Any(x => x.Endpoints.Any());
        
            var table = new Table(){Title = new TableTitle("Message Handlers"){Style = new Style(decoration:Decoration.Bold)}};
            table.AddColumn("Message Name");
            
            table.AddColumn("[bold]Message Type[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
            table.AddColumn("[bold]Handler.Method()[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
            table.AddColumn("Generated Type Name");

            if (hasStickys)
            {
                table.AddColumn("Endpoints");
            }

            // GH-5136. One row per handler chain, which is one per (message type, handler type)
            foreach (var chain in Chains)
            {
                var messageType = $"[bold]{chain.MessageType.NameInCode().EscapeMarkup()}[/]\n  [dim]{chain.MessageType.Namespace.EscapeMarkup()}[/]";
                var chainTypeName = chain.TypeName.EscapeMarkup();
                var messageTypeName = chain.MessageType.ToMessageTypeName().EscapeMarkup();
                var handlerType = FormatHandlerType(chain.Handlers);

                if (hasStickys)
                {
                    var endpoints = chain.Endpoints.Select(x => x.Uri.ToString()).Join(", ");
                    table.AddRow(messageTypeName, messageType, handlerType, chainTypeName, endpoints);
                }
                else
                {
                    table.AddRow(messageTypeName, messageType, handlerType, chainTypeName);
                }
            }

            AnsiConsole.Write(table);
        }

        return Task.CompletedTask;

        string FormatHandlerType(List<MethodCall> handlers)
        {
            var handlerType = handlers
                .Select(handler => $"[bold]{handler.HandlerType.NameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ").EscapeMarkup()})[/]\n  [dim]{handler.HandlerType.Namespace}[/]")
                .Join("\n");

            return handlerType;
        }
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