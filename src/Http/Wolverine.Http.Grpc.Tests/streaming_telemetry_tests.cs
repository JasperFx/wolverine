using System.Diagnostics;
using Shouldly;
using Wolverine.Http.Grpc;
using Wolverine.Runtime;
using Xunit;

namespace Http.Grpc.Tests;

public class streaming_telemetry_tests
{
    [Fact]
    public async Task with_telemetry_tracks_streamed_messages()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var _ = testListener.StartListening();

        var source = GenerateTestStream(5);

        // Act
        var results = new List<int>();
        await foreach (var item in source.WithTelemetry())
        {
            results.Add(item);
        }

        // Assert
        results.ShouldBe(new[] { 1, 2, 3, 4, 5 });
        testListener.StartedActivities.Count.ShouldBe(1);

        var activity = testListener.StartedActivities.First();
        activity.OperationName.ShouldBe(WolverineTracing.StreamingStarted);
        activity.GetTagItem(WolverineTracing.StreamingMessageType).ShouldBe("Int32");
        activity.GetTagItem(WolverineTracing.StreamingMessageCount).ShouldBe(5);
    }

    [Fact]
    public async Task with_telemetry_records_completion()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var disposable1 = testListener.StartListening();

        var source = GenerateTestStream(3);

        // Act
        await foreach (var item in source.WithTelemetry())
        {
            // Consume stream
        }

        // Assert
        var activity = testListener.StartedActivities.First();
        var events = activity.Events.ToList();
        events.Any(e => e.Name == WolverineTracing.StreamingCompleted).ShouldBeTrue();
    }

    [Fact]
    public async Task with_telemetry_records_errors()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var disposable2 = testListener.StartListening();

        var source = GenerateErrorStream();

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in source.WithTelemetry())
            {
                // This will throw
            }
        });

        var activity = testListener.StartedActivities.First();
        activity.Status.ShouldBe(ActivityStatusCode.Error);
        var events = activity.Events.ToList();
        events.Any(e => e.Name == WolverineTracing.StreamingError).ShouldBeTrue();
    }

    [Fact]
    public async Task with_telemetry_includes_correlation_id()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var disposable3 = testListener.StartListening();

        var correlationId = "test-correlation-123";
        var source = GenerateTestStream(2);

        // Act
        await foreach (var item in source.WithTelemetry(correlationId))
        {
            // Consume stream
        }

        // Assert
        var activity = testListener.StartedActivities.First();
        activity.GetTagItem(WolverineTracing.MessagingConversationId).ShouldBe(correlationId);
    }

    [Fact]
    public async Task with_telemetry_tracks_each_yielded_message()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var disposable4 = testListener.StartListening();

        var source = GenerateTestStream(3);

        // Act
        await foreach (var item in source.WithTelemetry())
        {
            // Consume stream
        }

        // Assert
        var activity = testListener.StartedActivities.First();
        var events = activity.Events.ToList();
        var yieldEvents = events.Where(e => e.Name == WolverineTracing.StreamingMessageYielded).ToList();
        yieldEvents.Count.ShouldBe(3);
    }

    [Fact]
    public async Task with_telemetry_handles_cancellation()
    {
        // Arrange
        var testListener = new TestActivityListener();
        using var disposable5 = testListener.StartListening();

        var cts = new CancellationTokenSource();
        var source = GenerateInfiniteStream();

        // Act
        var count = 0;
        try
        {
            await foreach (var item in source.WithTelemetry(cancellationToken: cts.Token))
            {
                count++;
                if (count == 3)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        count.ShouldBe(3);
        var activity = testListener.StartedActivities.First();
        activity.GetTagItem(WolverineTracing.StreamingMessageCount).ShouldBe(3);
    }

    private static async IAsyncEnumerable<int> GenerateTestStream(int count)
    {
        for (int i = 1; i <= count; i++)
        {
            await Task.Delay(1);
            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> GenerateErrorStream()
    {
        yield return 1;
        await Task.Delay(1);
        throw new InvalidOperationException("Test error");
    }

    private static async IAsyncEnumerable<int> GenerateInfiniteStream()
    {
        int i = 0;
        while (true)
        {
            await Task.Delay(1);
            yield return ++i;
        }
    }

    private class TestActivityListener : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> StartedActivities { get; } = new();

        public TestActivityListener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Wolverine",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = activity => StartedActivities.Add(activity)
            };
        }

        public IDisposable StartListening()
        {
            ActivitySource.AddActivityListener(_listener);
            return this;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
