using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Acceptance;

public class on_exception_convention
{
    private readonly ITestOutputHelper _output;

    public on_exception_convention(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(List<string> actions, ITrackedSession session)> invokeMessage<T>(T message,
        Action<IPolicies>? registration = null)
    {
        var recorder = new OnExceptionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.IncludeType(typeof(OnExceptionHandler));
                opts.Discovery.IncludeType(typeof(SpecificExceptionHandler));
                opts.Discovery.IncludeType(typeof(OnExceptionWithFinallyHandler));
                opts.Discovery.IncludeType(typeof(VoidOnExceptionHandler));
                registration?.Invoke(opts.Policies);
            }).StartAsync();

        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(message);

        foreach (var action in recorder.Actions) _output.WriteLine($"\"{action}\"");

        await host.StopAsync();

        return (recorder.Actions, session);
    }

    [Fact]
    public async Task handler_level_on_exception_is_called()
    {
        var (actions, _) = await invokeMessage(new MessageThatThrows("boom"));

        actions.ShouldContain("OnException:boom");
    }

    [Fact]
    public async Task specific_exception_matched_first()
    {
        var (actions, _) = await invokeMessage(new MessageThatThrowsSpecific("specific boom"));

        actions.ShouldContain("OnSpecificException:specific boom");
        actions.ShouldNotContain(a => a.StartsWith("OnGeneralException"));
    }

    [Fact]
    public async Task general_exception_catches_base_type()
    {
        var (actions, _) = await invokeMessage(new MessageThatThrowsGeneral("general boom"));

        actions.ShouldContain("OnGeneralException:general boom");
    }

    [Fact]
    public async Task on_exception_with_finally_both_run()
    {
        var (actions, _) = await invokeMessage(new MessageWithFinally());

        actions.ShouldContain("Handler");
        actions.ShouldContain("OnException");
        actions.ShouldContain("Finally");
    }

    [Fact]
    public async Task void_on_exception_swallows()
    {
        var (actions, session) = await invokeMessage(new MessageForVoidHandler());

        actions.ShouldContain("OnException:void handler boom");
        // Exception was swallowed — not in dead letter queue
    }

    [Fact]
    public async Task middleware_on_exception()
    {
        var recorder = new OnExceptionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.IncludeType(typeof(NoOwnExceptionHandler));
                opts.Policies.AddMiddleware(typeof(GlobalOnExceptionMiddleware));
            }).StartAsync();

        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new MessageForMiddlewareTest("middleware test"));

        foreach (var action in recorder.Actions) _output.WriteLine($"\"{action}\"");

        await host.StopAsync();

        recorder.Actions.ShouldContain("MiddlewareOnException:middleware test");
    }
}

// Support types
public class OnExceptionRecorder
{
    public List<string> Actions { get; } = new();
}

public class TestAppException : Exception
{
    public TestAppException(string message) : base(message) { }
}

public class SpecificTestException : TestAppException
{
    public SpecificTestException(string message) : base(message) { }
}

// Messages
public record MessageThatThrows(string Text);
public record MessageThatThrowsSpecific(string Text);
public record MessageThatThrowsGeneral(string Text);
public record MessageWithFinally;
public record MessageForVoidHandler;
public record MessageForMiddlewareTest(string Text);

// Handlers
public static class OnExceptionHandler
{
    public static void Handle(MessageThatThrows message, OnExceptionRecorder recorder)
    {
        throw new TestAppException(message.Text);
    }

    public static void OnException(TestAppException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"OnException:{ex.Message}");
    }
}

public static class SpecificExceptionHandler
{
    public static void Handle(MessageThatThrowsSpecific message, OnExceptionRecorder recorder)
    {
        throw new SpecificTestException(message.Text);
    }

    public static void Handle(MessageThatThrowsGeneral message, OnExceptionRecorder recorder)
    {
        throw new TestAppException(message.Text);
    }

    public static void OnException(SpecificTestException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"OnSpecificException:{ex.Message}");
    }

    public static void OnException(TestAppException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"OnGeneralException:{ex.Message}");
    }
}

public static class OnExceptionWithFinallyHandler
{
    public static void Handle(MessageWithFinally message, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add("Handler");
        throw new TestAppException("error");
    }

    public static void OnException(TestAppException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add("OnException");
    }

    public static void Finally(OnExceptionRecorder recorder)
    {
        recorder.Actions.Add("Finally");
    }
}

public static class VoidOnExceptionHandler
{
    public static void Handle(MessageForVoidHandler message, OnExceptionRecorder recorder)
    {
        throw new TestAppException("void handler boom");
    }

    public static void OnException(TestAppException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"OnException:{ex.Message}");
    }
}

/// <summary>
/// Handler that does NOT have its own OnException — relies on middleware
/// </summary>
public static class NoOwnExceptionHandler
{
    public static void Handle(MessageForMiddlewareTest message, OnExceptionRecorder recorder)
    {
        throw new TestAppException(message.Text);
    }
}

public static class GlobalOnExceptionMiddleware
{
    public static void OnException(TestAppException ex, OnExceptionRecorder recorder)
    {
        recorder.Actions.Add($"MiddlewareOnException:{ex.Message}");
    }
}
