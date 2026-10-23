using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

public class handler_type_activity_tagging : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly List<Activity> _capturedActivities = new();
    private ActivityListener _listener = null!;

    public async ValueTask InitializeAsync()
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

    public async ValueTask DisposeAsync()
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
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

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

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

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

        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

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

/// <summary>
/// GH-5133. The handler type tag used to be set only for a chain with exactly one handler call, so a saga (Start
/// and Handle on one type) and a chain combining several handler types got no tag at all.
/// </summary>
public class handler_type_activity_tagging_beyond_one_handler_call : IAsyncLifetime
{
    private readonly List<Activity> _capturedActivities = new();
    private ActivityListener _listener = null!;

    public ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_capturedActivities) _capturedActivities.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }

    private string[] handlerTypeTags()
    {
        lock (_capturedActivities)
        {
            return _capturedActivities
                .Select(a => a.GetTagItem(WolverineTracing.HandlerType) as string)
                .OfType<string>()
                .Distinct()
                .ToArray();
        }
    }

    [Fact]
    public async Task tags_a_saga_with_several_methods_for_one_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<TaggedSaga>();
            }).StartAsync(TestContext.Current.CancellationToken);

        // StartOrHandle-style: the saga has both Start and Handle for the same message
        await host.InvokeMessageAndWaitAsync(new TaggedSagaMessage(Guid.NewGuid()));

        handlerTypeTags().ShouldContain(typeof(TaggedSaga).FullNameInCode());
    }

    [Fact]
    public async Task tags_each_separated_chain_with_its_own_handler_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FirstTaggedModuleHandler>()
                    .IncludeType<SecondTaggedModuleHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity().IncludeExternalTransports()
            .PublishMessageAndWaitAsync(new TaggedModuleEvent(Guid.NewGuid()));

        var tags = handlerTypeTags();
        tags.ShouldContain(typeof(FirstTaggedModuleHandler).FullNameInCode());
        tags.ShouldContain(typeof(SecondTaggedModuleHandler).FullNameInCode());
    }

    [Fact]
    public async Task tags_a_combined_chain_with_every_handler_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The default ClassicCombineIntoOneLogicalHandler mode
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FirstTaggedModuleHandler>()
                    .IncludeType<SecondTaggedModuleHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.InvokeMessageAndWaitAsync(new TaggedModuleEvent(Guid.NewGuid()));

        var tag = handlerTypeTags().Single(x => x.Contains(nameof(FirstTaggedModuleHandler)));
        tag.ShouldContain(typeof(SecondTaggedModuleHandler).FullNameInCode());
    }
}

public record TaggedSagaMessage(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class TaggedSaga : Saga
{
    public Guid Id { get; set; }

    public static TaggedSaga Start(TaggedSagaMessage message) => new() { Id = message.Id };

    public void Handle(TaggedSagaMessage message)
    {
    }
}

public record TaggedModuleEvent(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class FirstTaggedModuleHandler
{
    public void Handle(TaggedModuleEvent message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class SecondTaggedModuleHandler
{
    public void Handle(TaggedModuleEvent message)
    {
    }
}
