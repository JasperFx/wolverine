using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Oakton.Descriptions;
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
        var table = new Table();
        table.AddColumn("Message Name");
        table.AddColumn("[bold]Message Type[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
        table.AddColumn("[bold]Handler.Method()[/]\n  [dim]namespace[/]", c => c.NoWrap = true);
        table.AddColumn("Generated Type Name");
        
        foreach (var chain in Chains)
        {
            var messageType = $"[bold]{chain.MessageType.NameInCode()}[/]\n  [dim]{chain.MessageType.Namespace}[/]";

            var handlerType = chain.Handlers.Select(handler =>
                    $"[bold]{handler.HandlerType.NameInCode()}.{handler.Method.Name}({handler.Method.GetParameters().Select(x => x.Name)!.Join(", ")})[/]\n  [dim]{handler.HandlerType.Namespace}[/]")
                .Join("\n");

            table.AddRow(WolverineMessageNaming.ToMessageTypeName(chain.MessageType), messageType, handlerType, chain.TypeName);
        }

        AnsiConsole.Render(table);

        return Task.CompletedTask;
    }
}