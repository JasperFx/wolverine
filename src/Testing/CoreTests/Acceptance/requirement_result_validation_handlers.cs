using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class requirement_result_validation_handlers
{
    [Fact]
    public async Task happy_path_with_requirement_result_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        RequirementResultHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new RequirementResultMessage(3));

        RequirementResultHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_requirement_result_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        RequirementResultHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new RequirementResultMessage(20));

        RequirementResultHandler.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task happy_path_with_async_requirement_result_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        AsyncRequirementResultHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new AsyncRequirementResultMessage(3));

        AsyncRequirementResultHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_async_requirement_result_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        AsyncRequirementResultHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new AsyncRequirementResultMessage(20));

        AsyncRequirementResultHandler.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task sad_path_with_empty_messages_requirement_result_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        EmptyMessagesRequirementResultHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new EmptyMessagesRequirementResultMessage(20));

        EmptyMessagesRequirementResultHandler.Handled.ShouldBeFalse();
    }
}

#region sample_requirement_result_validation

public record RequirementResultMessage(int Number);

public static class RequirementResultHandler
{
    public static RequirementResult Validate(RequirementResultMessage message)
    {
        if (message.Number > 10)
        {
            return new RequirementResult(HandlerContinuation.Stop, ["Number must be 10 or less"]);
        }

        return new RequirementResult(HandlerContinuation.Continue, []);
    }

    public static void Handle(RequirementResultMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion

#region sample_requirement_result_validation_async

public record AsyncRequirementResultMessage(int Number);

public static class AsyncRequirementResultHandler
{
    public static Task<RequirementResult> ValidateAsync(AsyncRequirementResultMessage message)
    {
        if (message.Number > 10)
        {
            return Task.FromResult(new RequirementResult(HandlerContinuation.Stop, ["Number must be 10 or less"]));
        }

        return Task.FromResult(new RequirementResult(HandlerContinuation.Continue, []));
    }

    public static void Handle(AsyncRequirementResultMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion

#region sample_requirement_result_validation_empty_messages

public record EmptyMessagesRequirementResultMessage(int Number);

public static class EmptyMessagesRequirementResultHandler
{
    public static RequirementResult Validate(EmptyMessagesRequirementResultMessage message)
    {
        if (message.Number > 10)
        {
            return new RequirementResult(HandlerContinuation.Stop, []);
        }

        return new RequirementResult(HandlerContinuation.Continue, []);
    }

    public static void Handle(EmptyMessagesRequirementResultMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion
