using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5355. An OnExceptionFilter method next to an OnException method becomes a `when (...)` filter on the
// generated catch clause, so a non-matching exception falls through to the sibling clauses
public class on_exception_filter_convention
{
    private static async Task<ConcurrentQueue<string>> invoke(FilterMessage message)
    {
        var recorder = new FilterRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FilteredAggregateHandler));
            }).StartAsync();

        await host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(message);
        await host.StopAsync();
        return recorder.Actions;
    }

    [Fact]
    public async Task matching_filter_is_handled_by_the_filtered_clause()
    {
        var actions = await invoke(new FilterMessage(true));
        actions.ShouldBe(["filtered"]);
    }

    [Fact]
    public async Task non_matching_filter_falls_through_to_the_sibling_clause()
    {
        var actions = await invoke(new FilterMessage(false));
        actions.ShouldBe(["general"]);
    }
}

public record FilterMessage(bool Matches);

public class FilterRecorder
{
    public ConcurrentQueue<string> Actions { get; } = new();
}

public static class FilteredAggregateHandler
{
    public static void Handle(FilterMessage message)
    {
        throw new AggregateException(message.Matches
            ? new InvalidTimeZoneException("match")
            : new InvalidOperationException("no match"));
    }

    public static void OnException(AggregateException ex, FilterRecorder recorder) => recorder.Actions.Enqueue("filtered");

    public static bool OnExceptionFilter(AggregateException ex) => ex.InnerException is InvalidTimeZoneException;

    public static void OnException(Exception ex, FilterRecorder recorder) => recorder.Actions.Enqueue("general");
}
