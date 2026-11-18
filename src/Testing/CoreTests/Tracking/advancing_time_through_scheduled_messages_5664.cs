using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

// GH-5664. ITrackedSession.PlayScheduledMessagesAsync(advanceBy:) moves the session's clock forward and plays only the
// scheduled messages that come due on the way, in due order, so a saga with a recurring poll and an overall deadline
// can be walked through step by step: the deadline fires once its time is reached, not with the first poll.
public class advancing_time_through_scheduled_messages_5664 : IAsyncLifetime
{
    // Shared with the application, so the saga reads the same "now" that the session advances
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    private IHost _host = null!;
    private DateTimeOffset _start;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ExportWatchSaga>()
                    .IncludeType(typeof(ExportOutcomeHandler));

                opts.Services.AddSingleton<TimeProvider>(_clock);
            }).StartAsync();

        _start = _clock.GetUtcNow();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<ITrackedSession> startWatching(Guid id, int readyAfterPolls)
        => _host.InvokeMessageAndWaitAsync(new WatchExportJob(id, readyAfterPolls));

    [Fact]
    public async Task the_start_schedules_the_first_poll_and_the_deadline_without_playing_them()
    {
        var session = await startWatching(Guid.NewGuid(), 100);

        session.Scheduled.SingleMessage<PollExportJob>().ShouldNotBeNull();
        session.Scheduled.SingleMessage<ExportJobDeadline>().ShouldNotBeNull();
        session.Executed.MessagesOf<PollExportJob>().ShouldBeEmpty();
        session.Clock.GetUtcNow().ShouldBe(_start);
    }

    [Fact]
    public async Task plays_only_the_messages_due_within_the_window()
    {
        var id = Guid.NewGuid();
        var session = await startWatching(id, 100);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 5.Minutes());

        // The first poll was due at +5 minutes; it scheduled the next one at +10, which stays scheduled
        session.Executed.MessagesOf<PollExportJob>().Count().ShouldBe(1);
        session.Scheduled.SingleMessage<PollExportJob>().ShouldNotBeNull();
        session.Executed.MessagesOf<ExportJobDeadline>().ShouldBeEmpty();
        session.Clock.GetUtcNow().ShouldBe(_start.Add(5.Minutes()));

        (await loadSaga(id))!.Polls.ShouldBe(1);
    }

    [Fact]
    public async Task an_empty_window_only_moves_the_clock_and_a_later_call_continues_from_there()
    {
        var id = Guid.NewGuid();
        var session = await startWatching(id, 100);

        // Nothing is due in the first three minutes
        session = await session.PlayScheduledMessagesAsync(advanceBy: 3.Minutes());
        session.Executed.MessagesOf<PollExportJob>().ShouldBeEmpty();
        session.Clock.GetUtcNow().ShouldBe(_start.Add(3.Minutes()));
        _clock.GetUtcNow().ShouldBe(_start.Add(3.Minutes()));

        // Two more minutes reach the first poll, scheduled by the start rather than by the last window
        session = await session.PlayScheduledMessagesAsync(advanceBy: 2.Minutes());
        session.Executed.MessagesOf<PollExportJob>().Count().ShouldBe(1);
        (await loadSaga(id))!.LastPolledAt.ShouldBe(_start.Add(5.Minutes()));
    }

    [Fact]
    public async Task messages_scheduled_while_playing_are_played_when_they_fall_in_the_window()
    {
        var id = Guid.NewGuid();
        var session = await startWatching(id, 100);

        // +5 plays the first poll, which schedules +10, which schedules +15 (outside the window)
        session = await session.PlayScheduledMessagesAsync(advanceBy: 12.Minutes());

        session.Executed.MessagesOf<PollExportJob>().Count().ShouldBe(2);
        session.Executed.MessagesOf<ExportJobDeadline>().ShouldBeEmpty();
        session.Clock.GetUtcNow().ShouldBe(_start.Add(12.Minutes()));

        var saga = await loadSaga(id);
        saga!.Polls.ShouldBe(2);
        saga.LastPolledAt.ShouldBe(_start.Add(10.Minutes()));
    }

    [Fact]
    public async Task the_saga_reads_the_due_time_of_the_message_it_is_handling()
    {
        var id = Guid.NewGuid();
        var session = await startWatching(id, 3);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 20.Minutes());

        // Ready on the third poll, at +15 minutes; nothing else plays after the saga completes
        var finished = session.Executed.SingleMessage<ExportJobFinished>();
        finished.Polls.ShouldBe(3);
        finished.At.ShouldBe(_start.Add(15.Minutes()));
        session.Executed.MessagesOf<ExportJobGaveUp>().ShouldBeEmpty();
        (await loadSaga(id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_deadline_fires_only_once_its_time_is_reached()
    {
        var id = Guid.NewGuid();

        #region sample_advancing_time_through_scheduled_messages

        // The host registers a TimeProvider the session can move (FakeTimeProvider, or anything with a public
        // Advance(TimeSpan)), so the saga reads the same "now" the session advances
        var session = await _host.InvokeMessageAndWaitAsync(new WatchExportJob(id, ReadyAfterPolls: 100));

        session = await session.PlayScheduledMessagesAsync(advanceBy: 30.Minutes());
        session.Executed.MessagesOf<ExportJobGaveUp>().ShouldBeEmpty();
        session.Executed.MessagesOf<PollExportJob>().Count().ShouldBe(6);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 29.Minutes());
        session.Executed.MessagesOf<ExportJobGaveUp>().ShouldBeEmpty();
        (await loadSaga(id))!.Polls.ShouldBe(11);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 1.Minutes());

        // The deadline was scheduled at the start, so it plays before the poll due at the same instant, and that
        // poll then finds no saga and is dropped like any timeout for a completed saga
        var gaveUp = session.Executed.SingleMessage<ExportJobGaveUp>();
        gaveUp.Polls.ShouldBe(11);
        gaveUp.At.ShouldBe(_start.Add(1.Hours()));

        #endregion
        (await loadSaga(id)).ShouldBeNull();
    }

    [Fact]
    public async Task plays_due_messages_in_due_order_within_one_window()
    {
        var id = Guid.NewGuid();
        var session = await startWatching(id, 100);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 2.Hours());

        var executed = session.Executed.Envelopes()
            .Select(x => x.Message)
            .Where(x => x is PollExportJob or ExportJobDeadline)
            .ToArray();

        // Eleven polls (+5 .. +55), then the deadline at +60, then the +60 poll which no longer has a saga
        executed.Take(11).ShouldAllBe(x => x is PollExportJob);
        executed[11].ShouldBeOfType<ExportJobDeadline>();
        session.Executed.SingleMessage<ExportJobGaveUp>().At.ShouldBe(_start.Add(1.Hours()));
        session.Clock.GetUtcNow().ShouldBe(_start.Add(2.Hours()));
    }

    [Fact]
    public async Task the_window_cannot_be_negative()
    {
        var session = await startWatching(Guid.NewGuid(), 100);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => session.PlayScheduledMessagesAsync(advanceBy: -1.Minutes()));
    }

    private Task<ExportWatchSaga?> loadSaga(Guid id)
    {
        var persistor = _host.Services.GetRequiredService<Wolverine.Persistence.Sagas.InMemorySagaPersistor>();
        return Task.FromResult(persistor.Load<ExportWatchSaga>(id));
    }
}

// The session keeps a clock of its own when the application has none it can move, so ordering and windows still
// work; absolute times the application computed from the real clock are read relative to when they were scheduled
public class advancing_time_without_a_shared_clock_5664 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReminderHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task plays_delayed_and_absolute_scheduled_messages_by_their_due_time()
    {
        var session = await _host.ExecuteAndWaitAsync(async c =>
        {
            await c.ScheduleAsync(new Reminder("in 30 minutes"), 30.Minutes());
            await c.ScheduleAsync(new Reminder("at an hour from now"), DateTimeOffset.UtcNow.AddHours(1));
            await c.ScheduleAsync(new Reminder("in 10 minutes"), 10.Minutes());
        });

        var start = session.Clock.GetUtcNow();

        session = await session.PlayScheduledMessagesAsync(advanceBy: 20.Minutes());
        session.Executed.MessagesOf<Reminder>().Select(x => x.Text).ShouldBe(["in 10 minutes"]);
        session.Clock.GetUtcNow().ShouldBe(start.Add(20.Minutes()));

        session = await session.PlayScheduledMessagesAsync(advanceBy: 30.Minutes());
        session.Executed.MessagesOf<Reminder>().Select(x => x.Text).ShouldBe(["in 30 minutes"]);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 15.Minutes());
        session.Executed.MessagesOf<Reminder>().Select(x => x.Text).ShouldBe(["at an hour from now"]);
    }

    [Fact]
    public async Task the_application_clock_is_not_moved_when_it_cannot_be()
    {
        var session = await _host.ExecuteAndWaitAsync(c => c.ScheduleAsync(new Reminder("later"), 5.Minutes()).AsTask());

        session = await session.PlayScheduledMessagesAsync(advanceBy: 5.Minutes());
        session.Executed.SingleMessage<Reminder>().Text.ShouldBe("later");

        // The real clock did not move an hour, only the session's own
        (session.Clock.GetUtcNow() - DateTimeOffset.UtcNow).ShouldBeGreaterThan(4.Minutes());
    }

    [Fact]
    public async Task an_explicit_clock_is_moved_and_carried_to_the_next_window()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var start = clock.GetUtcNow();

        var session = await _host.TrackActivity().UseClock(clock)
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new Reminder("later"), 5.Minutes()).AsTask());

        session.Clock.ShouldBeSameAs(clock);

        session = await session.PlayScheduledMessagesAsync(advanceBy: 3.Minutes());
        clock.GetUtcNow().ShouldBe(start.Add(3.Minutes()));
        session.Executed.MessagesOf<Reminder>().ShouldBeEmpty();

        session = await session.PlayScheduledMessagesAsync(advanceBy: 3.Minutes());
        clock.GetUtcNow().ShouldBe(start.Add(6.Minutes()));
        session.Executed.SingleMessage<Reminder>().Text.ShouldBe("later");
    }

    [Fact]
    public void a_clock_that_cannot_be_moved_is_refused()
    {
        Should.Throw<ArgumentException>(() => _host.TrackActivity().UseClock(TimeProvider.System));
    }
}

#region sample_polling_saga_with_a_deadline

public record WatchExportJob(Guid Id, int ReadyAfterPolls);

// The recurring poll and the overall deadline are both saga timeouts
public record PollExportJob(Guid Id) : TimeoutMessage(5.Minutes());

public record ExportJobDeadline(Guid Id) : TimeoutMessage(1.Hours());

public record ExportJobFinished(Guid Id, int Polls, DateTimeOffset At);

public record ExportJobGaveUp(Guid Id, int Polls, DateTimeOffset At);

public class ExportWatchSaga : Saga
{
    public Guid Id { get; set; }
    public int ReadyAfterPolls { get; set; }
    public int Polls { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }

    public static (ExportWatchSaga, PollExportJob, ExportJobDeadline) Start(WatchExportJob command)
        => (new ExportWatchSaga { Id = command.Id, ReadyAfterPolls = command.ReadyAfterPolls },
            new PollExportJob(command.Id),
            new ExportJobDeadline(command.Id));

    // The application reads time through TimeProvider, so a test can share its clock with the tracked session
    public object Handle(PollExportJob poll, TimeProvider time)
    {
        Polls++;
        LastPolledAt = time.GetUtcNow();

        if (Polls >= ReadyAfterPolls)
        {
            MarkCompleted();
            return new ExportJobFinished(Id, Polls, time.GetUtcNow());
        }

        return new PollExportJob(Id);
    }

    public ExportJobGaveUp Handle(ExportJobDeadline deadline, TimeProvider time)
    {
        MarkCompleted();
        return new ExportJobGaveUp(Id, Polls, time.GetUtcNow());
    }
}

#endregion

public static class ExportOutcomeHandler
{
    public static void Handle(ExportJobFinished finished)
    {
    }

    public static void Handle(ExportJobGaveUp gaveUp)
    {
    }
}

public record Reminder(string Text);

public static class ReminderHandler
{
    public static void Handle(Reminder reminder)
    {
    }
}

// A TimeProvider a test can move, like Microsoft.Extensions.Time.Testing's FakeTimeProvider: the tracked session
// moves any clock with a public Advance(TimeSpan) method
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
