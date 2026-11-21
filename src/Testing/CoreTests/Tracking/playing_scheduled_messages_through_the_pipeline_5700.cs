using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

// GH-5700. PlayScheduledMessagesAsync delivers a due local message to its queue as if its time had come. The queue's
// endpoint handles it with its own error policies: a failure is retried, a message that keeps failing is dead
// lettered, and the session waits for all of that instead of throwing the first exception at the test.
// playInline: true keeps the behaviour from before 7.0, where the message ran inline and its first failure was thrown.
public class playing_scheduled_messages_through_the_pipeline_5700 : IAsyncLifetime
{
    private IHost _host = null!;
    private ReportService _reports = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SendReportHandler));
                opts.Services.AddSingleton<ReportService>();

                // Two retries, then the dead letter queue
                opts.OnException<ReportServiceUnavailable>().RetryTimes(2);
            }).StartAsync();

        _reports = _host.Services.GetRequiredService<ReportService>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<ITrackedSession> scheduleTheReport()
        => _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new SendReport("monthly"), 1.Hours()).AsTask());

    [Fact]
    public async Task a_failure_is_retried_and_then_succeeds()
    {
        _reports.FailuresLeft = 2;
        var session = await scheduleTheReport();

        session = await session.PlayScheduledMessagesAsync(advanceBy: 1.Hours());

        session.MessageSucceeded.SingleMessage<SendReport>().Name.ShouldBe("monthly");
        session.MovedToErrorQueue.MessagesOf<SendReport>().ShouldBeEmpty();
        _reports.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task a_message_that_keeps_failing_ends_in_the_dead_letter_queue()
    {
        _reports.FailuresLeft = int.MaxValue;
        var session = await scheduleTheReport();

        session = await session.PlayScheduledMessagesAsync(advanceBy: 1.Hours());

        session.MovedToErrorQueue.SingleMessage<SendReport>().Name.ShouldBe("monthly");
        _reports.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task playing_everything_goes_through_the_pipeline_too()
    {
        _reports.FailuresLeft = 1;
        var session = await scheduleTheReport();

        session = await session.PlayScheduledMessagesAsync(10.Seconds());

        session.MessageSucceeded.SingleMessage<SendReport>().Name.ShouldBe("monthly");
        _reports.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task played_inline_the_first_failure_is_thrown_at_the_test()
    {
        _reports.FailuresLeft = int.MaxValue;
        var session = await scheduleTheReport();

        // InvokeAsync, as before 7.0: its inline retries run, and then the failure is thrown at the test instead of the
        // message going to the dead letter queue
        await Should.ThrowAsync<ReportServiceUnavailable>(() =>
            session.PlayScheduledMessagesAsync(advanceBy: 1.Hours(), playInline: true));

        _reports.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task playing_everything_inline_throws_the_first_failure_too()
    {
        _reports.FailuresLeft = int.MaxValue;
        var session = await scheduleTheReport();

        await Should.ThrowAsync<ReportServiceUnavailable>(() => session.PlayScheduledMessagesAsync(10.Seconds(), playInline: true));
        _reports.Attempts.ShouldBe(3);
    }
}

public record SendReport(string Name);

public class ReportServiceUnavailable() : Exception("The report service is unavailable");

// The outside report service. It fails as many times as a test says, then works
public class ReportService
{
    public int FailuresLeft { get; set; }
    public int Attempts { get; set; }
}

public static class SendReportHandler
{
    public static void Handle(SendReport command, ReportService reports)
    {
        reports.Attempts++;
        if (reports.FailuresLeft-- > 0) throw new ReportServiceUnavailable();
    }
}
