using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Acceptance;

public class middleware_usage
{
    private readonly ITestOutputHelper _output;

    public middleware_usage(ITestOutputHelper output)
    {
        _output = output;
    }

    protected async Task<List<string>> invokeMessage<T>(T message, Action<IPolicies> registration)
    {
        var recorder = new Recorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                registration(opts.Policies);
            }).StartAsync();

        await host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(message);

        foreach (var action in recorder.Actions) _output.WriteLine($"\"{action}\"");

        await host.StopAsync();

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
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(MessageMatchingMiddleware));
            });

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
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(MessageMatchingMiddleware));
            });

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
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(MessageMatchingMiddleware));
            });

        list.ShouldHaveTheSameElementsAs(
            "Handled TracedMessage"
        );
    }

    [Fact]
    public async Task conditional_filter_continue_sync()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 3, Batter = "George Brett" },
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>()
                    .AddMiddleware(typeof(StopIfGreaterThan5))
                    .AddMiddleware(typeof(StopIfGreaterThan20));
            });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number",
            "Evaluated Number",
            "RunsScoredMessage: George Brett"
        );
    }

    [Fact]
    public async Task conditional_filter_stop_sync()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 20, Batter = "George Brett" },
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(StopIfGreaterThan5));
            });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number"
        );
    }

    [Fact]
    public async Task conditional_filter_continue_async()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 3, Batter = "George Brett" },
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(StopIfGreaterThan5Async));
            });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number",
            "RunsScoredMessage: George Brett"
        );
    }

    [Fact]
    public async Task conditional_filter_stop_async()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 20, Batter = "George Brett" },
            handlers =>
            {
                handlers.ForMessagesOfType<NumberedMessage>().AddMiddleware(typeof(StopIfGreaterThan5Async));
            });

        list.ShouldHaveTheSameElementsAs(
            "Evaluated Number"
        );
    }

    [Fact]
    public async Task using_try_finally_with_one_middleware_happy_path()
    {
        // SimpleBeforeAfterFinally

        var list = await invokeMessage(new RunsScoredMessage { Number = 20, Batter = "George Brett" },
            handlers => { handlers.AddMiddleware(typeof(SimpleBeforeAfterFinally)); });

        list.ShouldHaveTheSameElementsAs(
            "SimpleBeforeAfterFinally.Before",
            "RunsScoredMessage: George Brett",
            "SimpleBeforeAfterFinally.After",
            "SimpleBeforeAfterFinally.Finally",
            "SimpleBeforeAfterFinally.FinallyAsync");
    }

    [Fact]
    public async Task using_try_finally_with_one_middleware_sad_path()
    {
        // SimpleBeforeAfterFinally

        var list = await invokeMessage(new RunsScoredMessage { Number = 200, Batter = "George Brett" },
            handlers => { handlers.AddMiddleware(typeof(SimpleBeforeAfterFinally)); });

        list.ShouldHaveTheSameElementsAs(
            "SimpleBeforeAfterFinally.Before",
            "RunsScoredMessage: George Brett",
            "SimpleBeforeAfterFinally.Finally",
            "SimpleBeforeAfterFinally.FinallyAsync");
    }

    [Fact]
    public async Task use_middleware_that_creates_type_in_before_that_is_used_in_after()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 5, Batter = "Willie Mays" },
            handlers => handlers.AddMiddleware<BeforeProducesUsedInAfter>());

        list.ShouldHaveTheSameElementsAs(
            "RunsScoredMessage: Willie Mays",
            "Got activity with name = Created from Middleware");
    }

    [Fact]
    public async Task explicitly_added_middleware_by_attribute()
    {
        var list = await invokeMessage(new ExplicitMiddlewareMessage("Steve Balboni"), _ => { });

        list.ShouldHaveTheSameElementsAs(
            "Created SimpleBeforeAndAfter",
        "SimpleBeforeAndAfter.Before",
        "Created SimpleBeforeAndAfterTask",
        "SimpleBeforeAndAfterTask.Before",
        "Name is Steve Balboni",
        "SimpleBeforeAndAfterTask.After",
        "SimpleBeforeAndAfter.After"
            );
    }

    [Fact]
    public async Task using_finally_only_middleware_happy_path()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 5, Batter = "Willie Wilson" },
            x => x.AddMiddleware<FinallyOnlyMiddleware>());

        list.Last().ShouldBe("Called Finally");
    }

    [Fact]
    public async Task using_finally_only_middleware_sad_path()
    {
        var list = await invokeMessage(new RunsScoredMessage { Number = 200, Batter = "Willie Wilson" },
            x => x.AddMiddleware<FinallyOnlyMiddleware>());

        list.Last().ShouldBe("Called Finally");
    }

    [Fact]
    public async Task use_attributes_to_explicitly_opt_into_implied_middleware()
    {
        var list = await invokeMessage(new JumpBall("Go!"), _ => { });

        list.ShouldHaveTheSameElementsAs(
            "line up",
            "Jump Ball",
            "Back on Defense"
            );
    }

    [Fact]
    public async Task use_implied_middleware_that_is_inner_type()
    {
        var list = await invokeMessage(new SnapBall("Go!"), x =>
        {
            x.AddMiddleware<MiddlewareWrapper.FootballMiddleware>();
        });

        list.ShouldHaveTheSameElementsAs(
            "Line up",
            "Snap Ball",
            "Score touchdown"
        );
    }

    [Fact]
    public async Task service_with_middleware_created_dependency_in_constructor()
    {
        var list = await invokeMessage(new MessageWithServiceDependingOnMiddlewareType(),
            handlers => handlers.AddMiddleware<MiddlewareUserCreatingMiddleware>());

        list.ShouldHaveTheSameElementsAs(
            "MiddlewareUserCreatingMiddleware.Before - Created User: TestUser",
            "Handler received User: TestUser and Service with User: TestUser"
        );
    }

    [Fact]
    public async Task service_only_with_middleware_created_dependency_in_constructor()
    {
        var list = await invokeMessage(new MessageWithServiceOnlyDependingOnMiddlewareType(),
            handlers => handlers.AddMiddleware<MiddlewareUserCreatingMiddleware>());

        list.ShouldHaveTheSameElementsAs(
            "MiddlewareUserCreatingMiddleware.Before - Created User: TestUser",
            "Handler received Service with User: TestUser"
        );
    }
}

public class MiddlewareActivity
{
    public string Name { get; set; }
    public bool Finished { get; set; }
}

public class BeforeProducesUsedInAfter
{
    public MiddlewareActivity Before()
    {
        return new MiddlewareActivity { Name = "Created from Middleware" };
    }

    public void After(MiddlewareActivity activity, Recorder recorder)
    {
        recorder.Actions.Add($"Got activity with name = {activity.Name}");
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

public class SimpleBeforeAfterFinally
{
    public SimpleBeforeAfterFinally(Recorder recorder)
    {
        Recorder = recorder;
    }

    public Recorder Recorder { get; }

    public Task BeforeAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAfterFinally.Before");
        return Task.CompletedTask;
    }

    public Task AfterAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAfterFinally.After");
        return Task.CompletedTask;
    }

    public void Finally()
    {
        Recorder.Actions.Add("SimpleBeforeAfterFinally.Finally");
    }

    public void FinallyAsync()
    {
        Recorder.Actions.Add("SimpleBeforeAfterFinally.FinallyAsync");
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

public class TracedMessage;

public class OtherTracedMessage;

public record ExplicitMiddlewareMessage(string Name);

public class FinallyOnlyMiddleware
{
    public void Finally(Recorder recorder)
    {
        recorder.Actions.Add("Called Finally");
    }
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

    [Middleware(typeof(SimpleBeforeAndAfter), typeof(SimpleBeforeAndAfterTask))]
    public void Handle(ExplicitMiddlewareMessage message, Recorder recorder)
    {
        recorder.Actions.Add($"Name is {message.Name}");
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

public class StopIfGreaterThan20
{
    public HandlerContinuation Before(NumberedMessage message, Recorder recorder)
    {
        recorder.Actions.Add("Evaluated Number");
        return message.Number > 20 ? HandlerContinuation.Stop : HandlerContinuation.Continue;
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

        if (message.Number > 100) throw new Exception("C'mon, that's silly, nobody scores 100 runs");
    }

    public void Handle(StrikeoutsMessage message, Recorder recorder)
    {
        recorder.Actions.Add($"{nameof(StrikeoutsMessage)}: {message.Pitcher}");
    }
}

public record JumpBall(string Name);

public record SnapBall(string Name);

public class BasketballHandler
{
    [WolverineBefore]
    public static void LineUp(Recorder recorder)
    {
        recorder.Actions.Add("line up");
    }

    public static void Handle(JumpBall command, Recorder recorder)
    {
        recorder.Actions.Add("Jump Ball");
    }

    [WolverineAfter]
    public static void BackOnDefense(Recorder recorder)
    {
        recorder.Actions.Add("Back on Defense");
    }
}

public class FootballHandler
{
    public static void Handle(SnapBall command, Recorder recorder)
    {
        recorder.Actions.Add("Snap Ball");
    }
}

public class MiddlewareWrapper
{
    public class FootballMiddleware
    {
        [WolverineBefore]
        public static void LineUp(Recorder recorder)
        {
            recorder.Actions.Add("Line up");
        }

        [WolverineAfter]
        public static void BackOnDefense(Recorder recorder)
        {
            recorder.Actions.Add("Score touchdown");
        }
    }
}

#region service_with_middleware_created_dependency_in_constructor

public class MessageWithServiceDependingOnMiddlewareType;
public class MessageWithServiceOnlyDependingOnMiddlewareType;

public class MiddlewareUser
{
    public string Name { get; set; }
}

public class ServiceWithMiddlewareUser
{
    public MiddlewareUser User { get; }

    public ServiceWithMiddlewareUser(MiddlewareUser user)
    {
        User = user;
    }
}

public class MiddlewareUserCreatingMiddleware
{
    public MiddlewareUser Before(Recorder recorder)
    {
        var user = new MiddlewareUser { Name = "TestUser" };
        recorder.Actions.Add($"MiddlewareUserCreatingMiddleware.Before - Created User: {user.Name}");
        return user;
    }
}

public class MessageWithServiceDependingOnMiddlewareTypeHandler
{
    public void Handle(MessageWithServiceDependingOnMiddlewareType message, MiddlewareUser user, ServiceWithMiddlewareUser service, Recorder recorder)
    {
        recorder.Actions.Add($"Handler received User: {user.Name} and Service with User: {service.User.Name}");
    }
}

public class MessageWithServiceOnlyDependingOnMiddlewareTypeHandler
{
    public void Handle(MessageWithServiceOnlyDependingOnMiddlewareType message, ServiceWithMiddlewareUser service, Recorder recorder)
    {
        recorder.Actions.Add($"Handler received Service with User: {service.User.Name}");
    }
}

#endregion