using JasperFx.CommandLine;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Configuration.Capabilities;

public class SagaFlowInput : NetCoreInput
{
    [Description("The saga to draw, by its class name or full name. Every saga when left out")]
    public string? SagaName { get; set; }

    [Description("Write the Markdown to this file instead of the console")]
    [FlagAlias("file", 'f')]
    public string? FileFlag { get; set; }

    [Description("Write only the Mermaid source, without the Markdown heading and code fence. Needs a single saga")]
    [FlagAlias("mermaid-only", true)]
    public bool MermaidOnlyFlag { get; set; }
}

/// <summary>
/// GH-5670. <c>dotnet run -- saga-flow [SagaName]</c>: each saga's flow as a Mermaid diagram, in Markdown that pastes
/// into a README or the docs. The <c>describe</c> command prints the same diagrams in its "Saga Flows" section.
/// </summary>
[Description("Draw each saga's flow -- messages in, the methods that handle them, what they send, schedule and append, NotFound and Compensate -- as a Mermaid diagram",
    Name = "saga-flow")]
public class SagaFlowCommand : JasperFxAsyncCommand<SagaFlowInput>
{
    public SagaFlowCommand()
    {
        Usage("Draw every saga");
        Usage("Draw one saga").Arguments(x => x.SagaName!);
    }

    public override async Task<bool> Execute(SagaFlowInput input)
    {
        using var host = input.BuildHost();
        var runtime = (WolverineRuntime)host.GetRuntime();

        // As describe does: the handler graph without listeners, durability agents or brokers
        await runtime.StartLightweightAsync();

        var sagas = ServiceCapabilities.ReadSagaDescriptors(runtime);
        if (sagas.Count == 0)
        {
            Console.WriteLine("This application has no sagas.");
            return false;
        }

        if (input.SagaName.IsNotEmpty())
        {
            var matched = sagas.Where(x => string.Equals(x.StateType.Name, input.SagaName, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(x.StateType.FullName, input.SagaName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count == 0)
            {
                Console.WriteLine($"This application has no saga named '{input.SagaName}'. Its sagas are {string.Join(", ", sagas.Select(x => x.StateType.FullName))}.");
                return false;
            }

            sagas = matched;
        }

        if (input.MermaidOnlyFlag && sagas.Count > 1)
        {
            Console.WriteLine($"--mermaid-only draws one saga, and this application has {sagas.Count}. Name the one to draw.");
            return false;
        }

        var text = input.MermaidOnlyFlag
            ? SagaFlowDiagram.ToMermaid(sagas[0])
            : string.Join("\n", sagas.Select(SagaFlowDiagram.ToMarkdown));

        if (input.FileFlag.IsNotEmpty())
        {
            await File.WriteAllTextAsync(input.FileFlag!, text);
            Console.WriteLine($"Wrote the flow of {string.Join(", ", sagas.Select(x => x.StateType.Name))} to {input.FileFlag!.ToFullPath()}");
        }
        else
        {
            Console.Write(text);
        }

        return true;
    }
}
