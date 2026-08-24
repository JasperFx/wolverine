using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Pubsub.Internal;
using Xunit;

namespace Wolverine.Pubsub.Tests;

// GH-4066. Exhausting MaxTotalAckExtension makes Pub/Sub redeliver a message into a SECOND, concurrent
// callback while the first is still running -- with no cancellation, no exception, and (before this) no log
// line of any kind. These cover the detection of that crossing.
public class ack_extension_watchdog_4066
{
    private static readonly Uri TheUri = new("pubsub://wolverine/ackext");

    [Fact]
    public async Task warns_when_a_delivery_outlives_the_ack_extension_budget()
    {
        var logger = new RecordingLogger();
        await using var watchdog = new AckExtensionWatchdog(TheUri, TimeSpan.FromMilliseconds(50), logger);

        watchdog.Track("message-1");

        await Task.Delay(200, TestContext.Current.CancellationToken);

        watchdog.CheckForExpiredDeliveries();

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("message-1");
        warning.ShouldContain("MaxTotalAckExtension");
    }

    [Fact]
    public async Task only_warns_once_for_the_same_delivery()
    {
        var logger = new RecordingLogger();
        await using var watchdog = new AckExtensionWatchdog(TheUri, TimeSpan.FromMilliseconds(50), logger);

        watchdog.Track("message-1");

        await Task.Delay(200, TestContext.Current.CancellationToken);

        watchdog.CheckForExpiredDeliveries();
        watchdog.CheckForExpiredDeliveries();
        watchdog.CheckForExpiredDeliveries();

        logger.Warnings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task does_not_warn_for_a_delivery_that_is_still_inside_the_budget()
    {
        var logger = new RecordingLogger();
        await using var watchdog = new AckExtensionWatchdog(TheUri, TimeSpan.FromMinutes(30), logger);

        watchdog.Track("message-1");

        await Task.Delay(100, TestContext.Current.CancellationToken);

        watchdog.CheckForExpiredDeliveries();

        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task does_not_warn_for_a_delivery_that_was_released_in_time()
    {
        var logger = new RecordingLogger();
        await using var watchdog = new AckExtensionWatchdog(TheUri, TimeSpan.FromMilliseconds(50), logger);

        var ticket = watchdog.Track("message-1");
        watchdog.Release(ticket);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        watchdog.CheckForExpiredDeliveries();

        logger.Warnings.ShouldBeEmpty();
        watchdog.InFlightCount.ShouldBe(0);
    }

    [Fact]
    public async Task the_background_timer_warns_without_anyone_calling_the_scan()
    {
        var logger = new RecordingLogger();

        // Budget/10 == 100ms, which clamps up to the 1s minimum scan interval
        await using var watchdog = new AckExtensionWatchdog(TheUri, TimeSpan.FromSeconds(1), logger);

        watchdog.Track("message-1");

        var expiration = DateTimeOffset.UtcNow.AddSeconds(15);
        while (logger.Warnings.Count == 0 && DateTimeOffset.UtcNow < expiration)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        logger.Warnings.ShouldHaveSingleItem().ShouldContain("message-1");
    }

    [Fact]
    public void scan_interval_is_clamped_at_both_ends()
    {
        var logger = new RecordingLogger();

        // A one hour budget would otherwise scan every six minutes
        new AckExtensionWatchdog(TheUri, TimeSpan.FromHours(1), logger).ScanInterval
            .ShouldBe(AckExtensionWatchdog.MaximumScanInterval);

        // A very short budget would otherwise spin
        new AckExtensionWatchdog(TheUri, TimeSpan.FromMilliseconds(10), logger).ScanInterval
            .ShouldBe(AckExtensionWatchdog.MinimumScanInterval);

        // In between, a tenth of the budget
        new AckExtensionWatchdog(TheUri, TimeSpan.FromSeconds(100), logger).ScanInterval
            .ShouldBe(TimeSpan.FromSeconds(10));
    }
}

internal class RecordingLogger : ILogger
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

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning)
        {
            return;
        }

        lock (_warnings)
        {
            _warnings.Add(formatter(state, exception));
        }
    }
}

// GH-4066 / GH-4067. The watchdog reads MaxTotalAckExtension straight off PubsubClientOptions, so it would
// still warn even if the value never reached the SubscriberClient. These guard the plumbing that makes the
// warning mean something -- and that the flow control bound is carried on EVERY listener, not just the
// batched one.
public class subscriber_settings_plumbing
{
    [Fact]
    public void carries_the_configured_ack_extension_budget_and_flow_control()
    {
        var options = new PubsubClientOptions
        {
            MaxTotalAckExtension = TimeSpan.FromMinutes(7),
            MaxOutstandingMessages = 42,
            MaxOutstandingByteCount = 4242
        };

        var settings = PubsubListener.BuildSubscriberSettings(options);

        settings.MaxTotalAckExtension.ShouldBe(TimeSpan.FromMinutes(7));
        settings.FlowControlSettings!.MaxOutstandingElementCount.ShouldBe(42);
        settings.FlowControlSettings.MaxOutstandingByteCount.ShouldBe(4242);
    }

    [Fact]
    public void the_default_ack_extension_budget_is_one_hour_and_is_chosen_deliberately()
    {
        // Documented in PubsubClientOptions.MaxTotalAckExtension: too low silently corrupts data via
        // concurrent duplicate execution, too high only delays redelivery of a wedged message. Erring
        // generous is the deliberate call, and it is set explicitly rather than inherited from the SDK.
        new PubsubClientOptions().MaxTotalAckExtension.ShouldBe(TimeSpan.FromHours(1));
        PubsubListener.BuildSubscriberSettings(new PubsubClientOptions())
            .MaxTotalAckExtension.ShouldBe(TimeSpan.FromHours(1));
    }
}
