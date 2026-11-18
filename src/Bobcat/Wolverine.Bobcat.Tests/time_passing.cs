using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat.Tests;

// GH-5664: WhenTimePasses moves the spec's clock forward and executes only the scheduled messages that come due on the
// way, in due order, as the next act. A saga that polls every five minutes under an hour's deadline can be walked
// through step by step: the deadline runs once the hour has passed, not with the first poll

public record WatchReport(Guid Id);

public record PollReport(Guid Id) : TimeoutMessage(5.Minutes());

public record ReportDeadline(Guid Id) : TimeoutMessage(1.Hours());

public record ReportAbandoned(Guid Id, int Polls, DateTimeOffset At);

public class ReportWatch : Saga
{
    public Guid Id { get; set; }
    public int Polls { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }

    public static (ReportWatch, PollReport, ReportDeadline) Start(WatchReport command)
        => (new ReportWatch { Id = command.Id }, new PollReport(command.Id), new ReportDeadline(command.Id));

    // The application shares the spec's clock, so this is the instant the poll came due
    public PollReport Handle(PollReport poll, TimeProvider time)
    {
        Polls++;
        LastPolledAt = time.GetUtcNow();
        return new PollReport(Id);
    }

    public ReportAbandoned Handle(ReportDeadline deadline, TimeProvider time)
    {
        MarkCompleted();
        return new ReportAbandoned(Id, Polls, time.GetUtcNow());
    }
}

public static class ReportAbandonedHandler
{
    public static void Handle(ReportAbandoned abandoned)
    {
    }
}

public sealed class ReportWatchHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ReportWatch))
                    .IncludeType(typeof(ReportAbandonedHandler));

                // The application reads the spec's clock
                opts.Services.ShareClock();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

[CollectionDefinition(nameof(ReportWatchCollection))]
public class ReportWatchCollection : ICollectionFixture<ReportWatchHost>;

[Collection(nameof(ReportWatchCollection))]
public class time_passing(ReportWatchHost app) : WolverineSpec(app.Host)
{
    private static readonly DateTimeOffset start = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task only_the_messages_due_within_the_time_are_executed()
    {
        var id = Guid.NewGuid();
        GivenTheTimeIs(start);
        await WhenReceived(new WatchReport(id));
        ThenMessageScheduled<PollReport>(5.Minutes());
        ThenMessageScheduled<ReportDeadline>(1.Hours());

        await WhenTimePasses(5.Minutes());

        ThenHandled<PollReport>();
        ThenNotHandled<ReportDeadline>();

        // The poll scheduled its successor, which waits for the next WhenTimePasses
        ThenMessageScheduled(new PollReport(id));
        await ThenSaga<ReportWatch>(id, Specify<ReportWatch>()
            .With(x => x.Polls, 1)
            .With(x => x.LastPolledAt, start.Add(5.Minutes())));
        Clock.GetUtcNow().ShouldBe(start.Add(5.Minutes()));
    }

    [Fact]
    public async Task time_with_nothing_due_executes_nothing_and_the_next_step_carries_on_from_there()
    {
        var id = Guid.NewGuid();
        GivenTheTimeIs(start);
        await WhenReceived(new WatchReport(id));

        await WhenTimePasses(3.Minutes());
        ThenNotHandled<PollReport>();
        Clock.GetUtcNow().ShouldBe(start.Add(3.Minutes()));

        // The first poll, scheduled by the act two steps back, comes due two minutes later
        await WhenTimePasses(2.Minutes());
        ThenHandled<PollReport>();
        await ThenSaga<ReportWatch>(id, Specify<ReportWatch>().With(x => x.LastPolledAt, start.Add(5.Minutes())));
    }

    [Fact]
    public async Task the_deadline_is_executed_once_its_time_is_reached()
    {
        var id = Guid.NewGuid();
        GivenTheTimeIs(start);
        await WhenReceived(new WatchReport(id));

        await WhenTimePasses(59.Minutes());
        ThenNotHandled<ReportDeadline>();
        await ThenSaga<ReportWatch>(id, Specify<ReportWatch>().With(x => x.Polls, 11));

        await WhenTimePasses(1.Minutes());
        ThenHandled<ReportDeadline>();
        ThenMessageSent(new ReportAbandoned(id, 11, start.Add(1.Hours())));
        await ThenNoSaga<ReportWatch>(id);
    }

    [Fact]
    public async Task the_step_renders_as_a_when_naming_the_time()
    {
        var id = Guid.NewGuid();
        GivenTheTimeIs(start);
        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new WatchReport(id));
            ThenMessageScheduled<ReportDeadline>();
            await WhenTimePasses(1.Hours().Add(30.Minutes()));
            ThenHandled<ReportDeadline>();
        });

        recording.GatheredFailures().ShouldBeNull();
        var passing = recording.Steps[2];
        passing.Keyword.ShouldBe("When");
        passing.Text.ShouldBe("1 hour 30 minutes passes");
    }

    [Fact]
    public async Task with_no_act_before_it_the_step_fails_and_stops_the_scenario()
    {
        var thrown = await Should.ThrowAsync<SpecCriticalException>(() => WhenTimePasses(5.Minutes()));
        thrown.Message.ShouldStartWith("There is no act whose scheduled messages could come due");
    }

    [Fact]
    public async Task time_only_passes_forward()
    {
        await WhenReceived(new WatchReport(Guid.NewGuid()));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => WhenTimePasses(-1.Minutes()));
    }
}
