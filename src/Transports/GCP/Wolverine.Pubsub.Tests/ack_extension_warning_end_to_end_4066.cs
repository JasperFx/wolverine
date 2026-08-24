using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-4066, end to end. Proves the ack extension watchdog is actually wired into a live listener: an
/// EndpointMode.Inline endpoint holds the subscriber callback for the whole handler, so a handler that outlives
/// MaxTotalAckExtension must produce a Warning rather than passing in silence.
///
/// What this DOES verify: the crossing is detected and logged while the handler is still running.
/// What this does NOT verify: that the emulator then delivers the concurrent duplicate. See the notes on the PR.
/// </summary>
public class ack_extension_warning_end_to_end_4066 : IAsyncLifetime
{
    private bool _skip;

    public async ValueTask InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task warns_when_an_inline_handler_outlives_the_ack_extension_budget()
    {
        if (_skip) return;

        var recorder = new RecordingLoggerProvider();

        SlowPubsubHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(recorder))
            .UseWolverine(opts =>
            {
                opts.UsePubsubTesting().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<SlowPubsubMessage>().ToPubsubTopic("ackext4066");

                opts.ListenToPubsubTopic("ackext4066")
                    .ProcessInline()
                    // Deliberately tiny so the crossing happens in seconds instead of the one hour default
                    .ConfigureListener(c => c.MaxTotalAckExtension = 2.Seconds());
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            await host.MessageBus().SendAsync(new SlowPubsubMessage());

            // The handler must actually be running, otherwise there is nothing to warn about
            await SlowPubsubHandler.Started.Task.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

            var expiration = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!recorder.Warnings.Any(x => x.Contains("MaxTotalAckExtension"))
                   && DateTimeOffset.UtcNow < expiration)
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }

            var warning = recorder.Warnings.FirstOrDefault(x => x.Contains("MaxTotalAckExtension"));
            warning.ShouldNotBeNull();
            warning.ShouldContain("CONCURRENT");
        }
        finally
        {
            // Let the handler go before the host tears down
            SlowPubsubHandler.Release.TrySetResult();
        }
    }
}

public record SlowPubsubMessage;

public static class SlowPubsubHandler
{
    public static TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static async Task Handle(SlowPubsubMessage message)
    {
        Started.TrySetResult();

        // Held well past the two second budget configured above, but released by the test rather than a
        // fixed sleep so the suite does not pay for it twice
        await Release.Task.WaitAsync(2.Minutes());
    }
}

internal class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
            {
                return _warnings.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new Recorder(this);
    }

    public void Dispose()
    {
    }

    private void record(string message)
    {
        lock (_warnings)
        {
            _warnings.Add(message);
        }
    }

    private class Recorder : ILogger
    {
        private readonly RecordingLoggerProvider _parent;

        public Recorder(RecordingLoggerProvider parent)
        {
            _parent = parent;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            _parent.record(formatter(state, exception));
        }
    }
}
