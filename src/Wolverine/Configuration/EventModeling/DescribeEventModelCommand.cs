using JasperFx.CodeGeneration;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Wolverine.Configuration.EventModeling;

public class DescribeEventModelInput : NetCoreInput
{
    [Description("Which of the application's Event Models to describe, when it hosts more than one")]
    [FlagAlias("model", true)]
    public string? ModelFlag { get; set; }

    [Description("Only the slices in this chapter")]
    [FlagAlias("chapter", true)]
    public string? ChapterFlag { get; set; }

    [Description("Only the slices in this domain")]
    [FlagAlias("domain", true)]
    public string? DomainFlag { get; set; }

    [Description("Only the slices with an open question (a hotspot) on them")]
    [FlagAlias("hotspots-only", true)]
    public bool HotspotsOnlyFlag { get; set; }
}

/// <summary>
///     <c>dotnet run -- describe-event-model</c> (GH-4917): the assembled Event Model for a person to read,
///     rather than the JSON <c>event-model</c> writes for tools. Built the same way, without starting the host.
///     Plain text when the output is redirected, so it pastes into an issue or a pull request.
/// </summary>
[Description("Describe the application's Event Model -- every slice by domain and chapter, with its trigger, aggregate, events, code, specifications and hotspots",
    Name = "describe-event-model")]
public class DescribeEventModelCommand : JasperFxAsyncCommand<DescribeEventModelInput>
{
    public DescribeEventModelCommand()
    {
        Usage("Describe the Event Model");
    }

    public override async Task<bool> Execute(DescribeEventModelInput input)
    {
        // As the event-model command: lightweight bootstrap, the host built but never started
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = input.BuildHost();
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var set = await WolverineEventModelExport.AssembleSetAsync(host.Services);

            EventModelDescriptor model;
            if (input.ModelFlag.IsNotEmpty())
            {
                if (set.Find(input.ModelFlag!) is not { } selected)
                {
                    Console.WriteLine($"This application hosts no Event Model named '{input.ModelFlag}'. It hosts {string.Join(", ", set.Models.Select(x => $"'{x.Name}'"))}.");
                    return false;
                }

                model = selected;
            }
            else
            {
                model = set.Sole ?? set.Collapse();
            }

            var filter = new EventModelReportFilter(input.ChapterFlag, input.DomainFlag, input.HotspotsOnlyFlag);
            if (Console.IsOutputRedirected)
            {
                Console.Write(EventModelReport.Plain(model, filter));
            }
            else
            {
                EventModelReport.Write(AnsiConsole.Console, model, filter);
            }

            return true;
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
