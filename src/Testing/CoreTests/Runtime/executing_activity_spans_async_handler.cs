using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

// The 2-arg HandlerPipeline.InvokeAsync is synchronous and starts the executing activity
// before handing off to the async 3-arg overload. When the activity was held in a `using`
// declaration there, it was disposed -- and therefore stopped -- at the inner overload's
// first suspension point, so the execution span only ever covered the synchronous prefix
// of message processing and none of a handler's actual async work. These tests pin the
// span to the handler's full execution.
public class executing_activity_spans_async_handler : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly List<Activity> _capturedActivities = new();
    private ActivityListener _listener = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_capturedActivities)
                {
                    _capturedActivities.Add(activity);
                }
            }
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
    public async Task execution_span_duration_covers_the_await_in_an_async_handler()
    {
        // Send (not invoke) so the message travels through the local queue's receiver and
        // the synchronous 2-arg HandlerPipeline.InvokeAsync -- the path where the premature
        // disposal lived. Executor.InvokeInlineAsync never had the defect.
        //
        // The first message pays for building and compiling the executor *inside* the
        // executing span's synchronous prefix, which is easily hundreds of milliseconds and
        // would let a truncated span still look "long enough". Warm up first, then measure
        // the second message, whose synchronous prefix is trivial.
        await _host.SendMessageAndWaitAsync(new SlowTracedMessage());

        lock (_capturedActivities)
        {
            _capturedActivities.Clear();
        }

        await _host.SendMessageAndWaitAsync(new SlowTracedMessage());

        // Give a moment for activities to be captured
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        List<Activity> executing;
        lock (_capturedActivities)
        {
            executing = _capturedActivities
                .Where(a => a.OperationName == typeof(SlowTracedMessage).ToMessageTypeName())
                .ToList();
        }

        var activity = executing.Single();

        // A little slack under the handler's 250ms delay to keep timer resolution out of the
        // assertion. The truncated shape stopped the span at the first suspension point, which
        // on a warmed-up executor is effectively immediate.
        activity.Duration.ShouldBeGreaterThan(200.Milliseconds());

        // And the span must not have ended before the handler finished.
        (activity.StartTimeUtc + activity.Duration).ShouldBeGreaterThanOrEqualTo(
            SlowTracedMessageHandler.LastCompletedUtc.UtcDateTime);
    }
}

public record SlowTracedMessage;

public static class SlowTracedMessageHandler
{
    public static DateTimeOffset LastCompletedUtc;

    public static async Task Handle(SlowTracedMessage message)
    {
        await Task.Delay(250.Milliseconds());
        LastCompletedUtc = DateTimeOffset.UtcNow;
    }
}
