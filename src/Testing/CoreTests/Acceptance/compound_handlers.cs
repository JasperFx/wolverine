using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
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

    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host.InvokeMessageAndWaitAsync(new MaybeBadThing(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
    }
    
    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing_through_outgoing_messages_in_middleware_signature()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host.InvokeMessageAndWaitAsync(new MaybeBadThing3(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
    }
    
    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing_through_outgoing_messages_in_external_middleware_signature()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host.InvokeMessageAndWaitAsync(new MaybeBadThing4(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
    }
    
    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<RejectYourThing>(host)
            .SendMessageAndWaitAsync(new MaybeBadThing(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
    }
    
    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing_with_outgoing_messages()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host.InvokeMessageAndWaitAsync(new MaybeBadThing2(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
    }
    
    [Fact]
    public async Task can_send_messages_from_before_methods_that_ultimately_stop_the_processing_with_OutgoingMessages_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        // Should fail validation if Number > 20
        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<RejectYourThing>(host)
            .SendMessageAndWaitAsync(new MaybeBadThing2(20));
        
        tracked.Received.SingleMessage<RejectYourThing>()
            .Number.ShouldBe(20);
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

public record MaybeBadThing(int Number);
public record MaybeBadThing2(int Number);

public record RejectYourThing(int Number);

#region sample_sending_messages_in_before_middleware

public static class MaybeBadThingHandler
{
    public static async Task<HandlerContinuation> ValidateAsync(MaybeBadThing thing, IMessageBus bus)
    {
        if (thing.Number > 10)
        {
            await bus.PublishAsync(new RejectYourThing(thing.Number));
            return HandlerContinuation.Stop;
        }

        return HandlerContinuation.Continue;
    }

    public static void Handle(MaybeBadThing message)
    {
        Debug.WriteLine("Got " + message);
    }
}

#endregion

public record MaybeBadThing3(int Number);

public static class MaybeBadThing3Handler
{
    public static (OutgoingMessages, HandlerContinuation) Validate(MaybeBadThing3 thing)
    {
        if (thing.Number > 10)
        {
            return ([new RejectYourThing(thing.Number)], HandlerContinuation.Stop);
        }

        return ([], HandlerContinuation.Continue);
    }

    public static void Handle(MaybeBadThing3 message)
    {
        Debug.WriteLine("Got " + message);
    }
}

#region sample_using_outgoing_messages_from_before_middleware

public static class MaybeBadThing2Handler
{
    public static (HandlerContinuation, OutgoingMessages) ValidateAsync(MaybeBadThing2 thing, IMessageBus bus)
    {
        if (thing.Number > 10)
        {
            return (HandlerContinuation.Stop, [new RejectYourThing(thing.Number)]);
        }

        return (HandlerContinuation.Continue, []);
    }

    public static void Handle(MaybeBadThing2 message)
    {
        Debug.WriteLine("Got " + message);
    }
}

#endregion

public static class RejectYourThingHandler
{
    public static void Handle(RejectYourThing thing)
    {
        Debug.WriteLine("Got " + thing);
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

#region sample_send_messages_through_outgoing_messages_with_external_middleware

public record MaybeBadThing4(int Number);

public static class MaybeBadThing4Middleware
{
    public static (OutgoingMessages, HandlerContinuation) Validate(MaybeBadThing4 thing)
    {
        if (thing.Number > 10)
        {
            return ([new RejectYourThing(thing.Number)], HandlerContinuation.Stop);
        }

        return ([], HandlerContinuation.Continue);
    }
}

[Middleware(typeof(MaybeBadThing4Middleware))]
public static class MaybeBadThing4Handler
{
    public static void Handle(MaybeBadThing4 message)
    {
        Debug.WriteLine("Got " + message);
    }
}

#endregion