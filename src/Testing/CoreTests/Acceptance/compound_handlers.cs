using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class compound_handlers
{
    [Fact]
    public async Task use_before_and_after_compound_handler()
    {
        var tracer = new Tracer();
        
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Services.AddSingleton(tracer))
            .StartAsync();

        await host.InvokeMessageAndWaitAsync(new AssignTask("green"));
        
        tracer.Messages.ShouldContain("Load");
        tracer.Messages.ShouldContain("LoadAsync");
        tracer.Messages.ShouldContain("PostProcess");
        tracer.Messages.ShouldContain("PostProcessAsync");
    }
}

public static class CompoundHandler
{
    public static Thing Load(AssignTask command, Tracer tracer)
    {
        tracer.Messages.Add("Load");
        return new Thing(command.TaskId);
    }

    public static Task<Tool> LoadAsync(AssignTask command, Tracer tracer)
    {
        tracer.Messages.Add("LoadAsync");
        return Task.FromResult(new Tool(command.TaskId));
    }

    public static void Handle(AssignTask command, Thing thing, Tool tool)
    {
        command.TaskId.ShouldBe(thing.TaskId);
        command.TaskId.ShouldBe(tool.TaskId);

        command.Handled = true;
        thing.Handled = true;
        tool.Handled = true;
    }

    public static void PostProcess(Thing thing, Tracer tracer)
    {
        tracer.Messages.Add("PostProcess");
        thing.Handled.ShouldBeTrue();
    }

    public static Task PostProcessAsync(Tool tool, Tracer tracer)
    {
        tracer.Messages.Add("PostProcessAsync");
        tool.Handled.ShouldBeTrue();
        return Task.CompletedTask;
    }
}

public class Tracer
{
    public readonly List<string> Messages = new();
}

public record Thing(string TaskId)
{
    public bool Handled { get; set; }
}

public record Tool(string TaskId)
{
    public bool Handled { get; set; }
}

public record AssignTask(string TaskId)
{
    public bool Handled { get; set; }
}