using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class simple_validation_handlers
{
    [Fact]
    public async Task happy_path_with_ienumerable_string_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationEnumerableHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateEnumerableMessage(3));

        SimpleValidationEnumerableHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_ienumerable_string_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationEnumerableHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateEnumerableMessage(20));

        SimpleValidationEnumerableHandler.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task happy_path_with_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationStringArrayHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateStringArrayMessage(3));

        SimpleValidationStringArrayHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationStringArrayHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateStringArrayMessage(20));

        SimpleValidationStringArrayHandler.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task happy_path_with_async_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationAsyncHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateAsyncMessage(3));

        SimpleValidationAsyncHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_async_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationAsyncHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateAsyncMessage(20));

        SimpleValidationAsyncHandler.Handled.ShouldBeFalse();
    }

    [Fact]
    public async Task happy_path_with_valuetask_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationValueTaskHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateValueTaskMessage(3));

        SimpleValidationValueTaskHandler.Handled.ShouldBeTrue();
    }

    [Fact]
    public async Task sad_path_with_valuetask_string_array_validate()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        SimpleValidationValueTaskHandler.Handled = false;

        await host.InvokeMessageAndWaitAsync(new SimpleValidateValueTaskMessage(20));

        SimpleValidationValueTaskHandler.Handled.ShouldBeFalse();
    }
}

#region sample_simple_validation_ienumerable

public record SimpleValidateEnumerableMessage(int Number);

public static class SimpleValidationEnumerableHandler
{
    public static IEnumerable<string> Validate(SimpleValidateEnumerableMessage message)
    {
        if (message.Number > 10)
        {
            yield return "Number must be 10 or less";
        }
    }

    public static void Handle(SimpleValidateEnumerableMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion

#region sample_simple_validation_string_array

public record SimpleValidateStringArrayMessage(int Number);

public static class SimpleValidationStringArrayHandler
{
    public static string[] Validate(SimpleValidateStringArrayMessage message)
    {
        if (message.Number > 10)
        {
            return ["Number must be 10 or less"];
        }

        return [];
    }

    public static void Handle(SimpleValidateStringArrayMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion

#region sample_simple_validation_async

public record SimpleValidateAsyncMessage(int Number);

public static class SimpleValidationAsyncHandler
{
    public static Task<string[]> ValidateAsync(SimpleValidateAsyncMessage message)
    {
        if (message.Number > 10)
        {
            return Task.FromResult(new[] { "Number must be 10 or less" });
        }

        return Task.FromResult(Array.Empty<string>());
    }

    public static void Handle(SimpleValidateAsyncMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion

#region sample_simple_validation_valuetask

public record SimpleValidateValueTaskMessage(int Number);

public static class SimpleValidationValueTaskHandler
{
    public static ValueTask<string[]> ValidateAsync(SimpleValidateValueTaskMessage message)
    {
        if (message.Number > 10)
        {
            return new ValueTask<string[]>(new[] { "Number must be 10 or less" });
        }

        return new ValueTask<string[]>(Array.Empty<string>());
    }

    public static void Handle(SimpleValidateValueTaskMessage message)
    {
        Debug.WriteLine("Handled " + message);
        Handled = true;
    }

    public static bool Handled { get; set; }
}

#endregion
