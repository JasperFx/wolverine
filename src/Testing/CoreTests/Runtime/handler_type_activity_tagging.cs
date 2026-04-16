using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

public class handler_type_activity_tagging : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly List<Activity> _capturedActivities = new();
    private ActivityListener _listener = null!;

    public async Task InitializeAsync()
    {
        // Set up an ActivityListener to capture Wolverine activities
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _capturedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();
    }

    public async Task DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task should_tag_handler_type_on_activity_for_message_handler()
    {
        await _host.InvokeMessageAndWaitAsync(new TracingTestMessage("hello"));

        // Give a moment for activities to be captured
        await Task.Delay(100.Milliseconds());

        var handlerActivities = _capturedActivities
            .Where(a => a.GetTagItem(WolverineTracing.HandlerType) != null)
            .ToList();

        handlerActivities.ShouldNotBeEmpty();

        var handlerTypeTag = handlerActivities.First()
            .GetTagItem(WolverineTracing.HandlerType) as string;
        handlerTypeTag.ShouldNotBeNull();
        handlerTypeTag.ShouldContain(nameof(TracingTestMessageHandler));
    }

    [Fact]
    public async Task should_tag_message_handler_on_activity_for_message_handler()
    {
        await _host.InvokeMessageAndWaitAsync(new TracingTestMessage("hello"));

        await Task.Delay(100.Milliseconds());

        var handlerActivities = _capturedActivities
            .Where(a => a.GetTagItem(WolverineTracing.MessageHandler) != null)
            .ToList();

        handlerActivities.ShouldNotBeEmpty();

        var messageHandlerTag = handlerActivities.First()
            .GetTagItem(WolverineTracing.MessageHandler) as string;
        messageHandlerTag.ShouldNotBeNull();
        messageHandlerTag.ShouldContain(nameof(TracingTestMessageHandler));
    }

    [Fact]
    public async Task handler_type_and_message_handler_tags_should_have_same_value()
    {
        await _host.InvokeMessageAndWaitAsync(new TracingTestMessage("hello"));

        await Task.Delay(100.Milliseconds());

        var activity = _capturedActivities
            .FirstOrDefault(a => a.GetTagItem(WolverineTracing.HandlerType) != null);

        activity.ShouldNotBeNull();

        var handlerType = activity.GetTagItem(WolverineTracing.HandlerType) as string;
        var messageHandler = activity.GetTagItem(WolverineTracing.MessageHandler) as string;

        handlerType.ShouldBe(messageHandler);
    }
}

public record TracingTestMessage(string Text);

public static class TracingTestMessageHandler
{
    public static void Handle(TracingTestMessage message)
    {
        // no-op handler for tracing tests
    }
}
