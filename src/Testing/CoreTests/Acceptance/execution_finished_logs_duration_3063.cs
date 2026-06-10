using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-3063: the "Finished processing" execution log emitted from Executor.ExecuteAsync (the
// transport-received path) now carries the handler's execution duration in milliseconds, both in
// the formatted message ("...executed in {Duration} ms") and as a structured "Duration" property
// so it can be filtered in structured logs to find slow handlers.
public class execution_finished_logs_duration_3063
{
    [Fact]
    public async Task finished_processing_log_includes_a_nonzero_duration()
    {
        var logger = new StructuredCapturingLogger();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
            })
            .StartAsync();

        // Route through the worker (Executor.ExecuteAsync), not the inline invoke path
        await host.SendMessageAndWaitAsync(new DurationTestMessage("hello"));

        var entry = logger.Entries.FirstOrDefault(e =>
            e.Message.Contains("Finished processing") && e.Message.Contains(nameof(DurationTestMessage)));

        entry.ShouldNotBeNull();

        // Formatted message reads "...executed in {Duration} ms"
        entry.Message.ShouldContain("executed in");
        entry.Message.ShouldContain(" ms");

        // The structured "Duration" property is present and non-zero
        var duration = entry.State.Single(pair => pair.Key == "Duration").Value;
        Convert.ToInt64(duration).ShouldBeGreaterThan(0);
    }
}

public record DurationTestMessage(string Text);

public static class DurationTestMessageHandler
{
    public static async Task Handle(DurationTestMessage message)
    {
        // Sleep long enough that the elapsed-ms timer rounds to a positive integer
        await Task.Delay(25);
    }
}

/// <summary>
/// Captures each log entry's formatted message together with its structured state so tests can
/// assert on individual log properties, not just the rendered string.
/// </summary>
internal class StructuredCapturingLogger : ILogger
{
    public record Entry(LogLevel Level, string Message,
        IReadOnlyList<KeyValuePair<string, object?>> State, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>
                    ?? Array.Empty<KeyValuePair<string, object?>>();

        lock (Entries)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception), pairs.ToList(), exception));
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
