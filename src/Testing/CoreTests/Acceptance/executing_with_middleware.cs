using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Acceptance;

public class executing_with_middleware
{
    private readonly ITestOutputHelper _output;

    public executing_with_middleware(ITestOutputHelper output)
    {
        _output = output;
    }

    protected async Task<List<string>> invokeMessage<T>(T message, Action<HandlerGraph> registration)
    {
        var recorder = new Recorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                registration(opts.HandlerGraph);
            }).StartAsync();

        await host.InvokeMessageAndWaitAsync(message);

        foreach (var action in recorder.Actions) _output.WriteLine($"\"{action}\"");

        return recorder.Actions;
    }

    [Fact]
    public async Task execute_simple_before_and_after()
    {
        var list = await invokeMessage(new TracedMessage(), handlers => handlers.AddMiddleware<SimpleBeforeAndAfter>());

        list.ShouldHaveTheSameElementsAs("Created SimpleBeforeAndAfter", "SimpleBeforeAndAfter.Before",
            "Handled TracedMessage", "SimpleBeforeAndAfter.After");
    }

    [Fact]
    public async Task execute_simple_before_and_after_with_static()
    {
        var list = await invokeMessage(new TracedMessage(),
            handlers => handlers.AddMiddleware(typeof(SimpleStaticBeforeAndAfter)));

        list.ShouldHaveTheSameElementsAs("SimpleStaticBeforeAndAfter.Before",
            "Handled TracedMessage", "SimpleStaticBeforeAndAfter.After");
    }

    [Fact]
    public async Task execute_simple_before_and_after_async()
    {
        var list = await invokeMessage(new TracedMessage(),
            handlers => handlers.AddMiddleware<SimpleBeforeAndAfterTask>());

        list.ShouldHaveTheSameElementsAs("Created SimpleBeforeAndAfterTask", "SimpleBeforeAndAfterTask.Before",
            "Handled TracedMessage", "SimpleBeforeAndAfterTask.After");
    }

    [Fact]
    public async ValueTask execute_simple_before_and_after_async_with_ValueTask()
    {
        var list = await invokeMessage(new TracedMessage(),
            handlers => handlers.AddMiddleware<SimpleBeforeAndAfterValueTask>());

        list.ShouldHaveTheSameElementsAs("Created SimpleBeforeAndAfterValueTask",
            "SimpleBeforeAndAfterValueTask.Before", "Handled TracedMessage", "SimpleBeforeAndAfterValueTask.After");
    }

    [Fact]
    public async Task use_multiple_middleware()
    {
        var list = await invokeMessage(new TracedMessage(), handlers =>
        {
            handlers.AddMiddleware<SimpleBeforeAndAfter>();
            handlers.AddMiddleware<SimpleBeforeAndAfterTask>();
            handlers.AddMiddleware<SimpleBeforeAndAfterValueTask>();
        });

        list.ShouldHaveTheSameElementsAs(
            "Created SimpleBeforeAndAfter",
            "SimpleBeforeAndAfter.Before",
            "Created SimpleBeforeAndAfterTask",
            "SimpleBeforeAndAfterTask.Before",
            "Created SimpleBeforeAndAfterValueTask",
            "SimpleBeforeAndAfter.BeforeValueTask",
            "Handled TracedMessage",
            "SimpleBeforeAndAfter.AfterValueTask",
            "SimpleBeforeAndAfterTask.After",
            "SimpleBeforeAndAfter.After"
        );
    }

    [Fact]
    public async Task use_multiple_middleware_but_filter()
    {
        var list = await invokeMessage(new TracedMessage(), handlers =>
        {
            handlers.AddMiddleware<SimpleBeforeAndAfter>();
            handlers.AddMiddleware<SimpleBeforeAndAfterTask>(_ => _.MessageType.Name != nameof(TracedMessage));
            handlers.AddMiddleware<SimpleBeforeAndAfterValueTask>();
        });

        list.ShouldHaveTheSameElementsAs(
            "Created SimpleBeforeAndAfter",
            "SimpleBeforeAndAfter.Before",
            "Created SimpleBeforeAndAfterValueTask",
            "SimpleBeforeAndAfter.BeforeValueTask",
            "Handled TracedMessage",
            "SimpleBeforeAndAfter.AfterValueTask",
            "SimpleBeforeAndAfter.After"
        );
    }

    [Fact]
    public async Task pass_objects_from_and_to_middleware()
    {
        var list = await invokeMessage(new TracedMessage(),
            handlers => { handlers.AddMiddleware<DisposableSpecialMiddleware>(); });

        list.ShouldHaveTheSameElementsAs(
            "Created DisposableSpecialMiddleware",
            "DisposableSpecialMiddleware.Before",
            "Handled TracedMessage",
            "DisposableSpecialMiddleware.After",
            "Disposed DisposableThing",
            "Disposing DisposableSpecialMiddleware"
        );
    }

    [Fact]
    public async Task apply_by_message_type_middleware_positive1()
    {
        var list = await invokeMessage(new StrikeoutsMessage { Number = 3, Pitcher = "Bret Saberhagen" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(MessageMatchingMiddleware)); });

        list.ShouldHaveTheSameElementsAs(
            "Before number 3",
            "StrikeoutsMessage: Bret Saberhagen",
            "After number 3"
        );
    }


    [Fact]
    public async Task apply_by_message_type_middleware_positive2()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 5, Batter = "George Brett" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(MessageMatchingMiddleware)); });

        list.ShouldHaveTheSameElementsAs(
            "Before number 5",
            "RunsScoredMessage: George Brett",
            "After number 5"
        );
    }


    [Fact]
    public async Task apply_by_message_type_middleware_negative_match()
    {
        var list = await invokeMessage(new TracedMessage(),
            handlers => { handlers.AddMiddlewareByMessageType(typeof(MessageMatchingMiddleware)); });

        list.ShouldHaveTheSameElementsAs(
            "Handled TracedMessage"
        );
    }

    [Fact]
    public async Task conditional_filter_continue_sync()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 3, Batter = "George Brett" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(StopIfGreaterThan5)); });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number",
            "RunsScoredMessage: George Brett"
        );
    }


    [Fact]
    public async Task conditional_filter_stop_sync()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 20, Batter = "George Brett" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(StopIfGreaterThan5)); });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number"
        );
    }

    [Fact]
    public async Task conditional_filter_continue_async()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 3, Batter = "George Brett" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(StopIfGreaterThan5Async)); });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number",
            "RunsScoredMessage: George Brett"
        );
    }


    [Fact]
    public async Task conditional_filter_stop_async()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 20, Batter = "George Brett" },
            handlers => { handlers.AddMiddlewareByMessageType(typeof(StopIfGreaterThan5Async)); });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number"
        );
    }
}

public class SimpleBeforeAndAfter
{
    public SimpleBeforeAndAfter(Recorder recorder)
    {
        recorder.Actions.Add("Created SimpleBeforeAndAfter");
    }

    public void Before(Recorder recorder)
    {
        recorder.Actions.Add("SimpleBeforeAndAfter.Before");
    }

    public void After(Recorder recorder)
    {
        recorder.Actions.Add("SimpleBeforeAndAfter.After");
    }
}

public class DisposableSpecialMiddleware : IDisposable
{
    private readonly Recorder _recorder;
    private DisposableThing _thing;

    public DisposableSpecialMiddleware(Recorder recorder)
    {
        _recorder = recorder;

        _recorder.Actions.Add("Created DisposableSpecialMiddleware");
    }

    public void Dispose()
    {
        _recorder.Actions.Add("Disposing DisposableSpecialMiddleware");
    }

    public DisposableThing Before(Recorder recorder)
    {
        recorder.Actions.Add("DisposableSpecialMiddleware.Before");
        _thing = new DisposableThing(recorder);

        return _thing;
    }


    public ValueTask After(DisposableThing thing)
    {
        _thing.ShouldBeSameAs(thing);
        _recorder.Actions.Add("DisposableSpecialMiddleware.After");

        return ValueTask.CompletedTask;
    }
}

public class DisposableThing : IDisposable
{
    private readonly Recorder _recorder;

    public DisposableThing(Recorder recorder)
    {
        _recorder = recorder;
    }

    public void Dispose()
    {
        _recorder.Actions.Add("Disposed DisposableThing");
    }
}

public static class SimpleStaticBeforeAndAfter
{
    public static void Before(Recorder recorder)
    {
        recorder.Actions.Add("SimpleStaticBeforeAndAfter.Before");
    }

    public static void After(Recorder recorder)
    {
        recorder.Actions.Add("SimpleStaticBeforeAndAfter.After");
    }
}

public class SimpleBeforeAndAfterTask
{
    public SimpleBeforeAndAfterTask(Recorder recorder)
    {
        Recorder = recorder;
        Recorder.Actions.Add("Created SimpleBeforeAndAfterTask");
    }

    public Recorder Recorder { get; }

    public Task BeforeAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAndAfterTask.Before");
        return Task.CompletedTask;
    }

    public Task AfterAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAndAfterTask.After");
        return Task.CompletedTask;
    }
}

public class SimpleBeforeAndAfterValueTask
{
    public SimpleBeforeAndAfterValueTask(Recorder recorder)
    {
        Recorder = recorder;
        Recorder.Actions.Add("Created SimpleBeforeAndAfterValueTask");
    }

    public Recorder Recorder { get; }

    public ValueTask BeforeAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAndAfter.BeforeValueTask");
        return ValueTask.CompletedTask;
    }

    public ValueTask AfterAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAndAfter.AfterValueTask");
        return ValueTask.CompletedTask;
    }
}

public class Recorder
{
    public readonly List<string> Actions = new();
}

public class TracedMessage
{
}

public class OtherTracedMessage
{
}

public class TracedMessageHandler
{
    public void Handle(TracedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Handled TracedMessage");
    }

    public void Handle(OtherTracedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Handled OtherTracedMessage");
    }
}

public abstract class NumberedMessage
{
    public int Number { get; set; }
}

public class StopIfGreaterThan5
{
    public HandlerContinuation Before(NumberedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Evaluated Number");
        return message.Number > 5 ? HandlerContinuation.Stop : HandlerContinuation.Continue;
    }
}

public class StopIfGreaterThan5Async
{
    public Task<HandlerContinuation> BeforeAsync(NumberedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Evaluated Number");
        var result = message.Number > 5 ? HandlerContinuation.Stop : HandlerContinuation.Continue;
        return Task.FromResult(result);
    }
}

public class RunsScoredMessage : NumberedMessage
{
    public string Batter { get; set; }
}

public class StrikeoutsMessage : NumberedMessage
{
    public string Pitcher { get; set; }
}

public static class MessageMatchingMiddleware
{
    public static void Before(NumberedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Before number " + message.Number);
    }

    public static void After(NumberedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("After number " + message.Number);
    }
}

public class BaseballHandler
{
    public void Handle(RunsScoredMessage message, Recorder recorder)
    {
        recorder.Actions.Add($"{nameof(RunsScoredMessage)}: {message.Batter}");
    }

    public void Handle(StrikeoutsMessage message, Recorder recorder)
    {
        recorder.Actions.Add($"{nameof(StrikeoutsMessage)}: {message.Pitcher}");
    }
}