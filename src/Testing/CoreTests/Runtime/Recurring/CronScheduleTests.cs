using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Runtime.Recurring;

public class CronScheduleTests
{
    // A real DST-observing zone, spelled per-platform. The DST cases are the entire reason the
    // schedule carries a TimeZoneInfo, so they must run against a zone that actually transitions.
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    [Fact]
    public void computes_the_next_occurrence_in_utc_by_default()
    {
        var schedule = new CronSchedule("0 9 * * *");

        var next = schedule.NextOccurrence(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));

        next.ShouldBe(new DateTimeOffset(2026, 1, 16, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void the_occurrence_is_strictly_after_the_supplied_instant()
    {
        var schedule = new CronSchedule("0 9 * * *");

        // Exactly 09:00 asks for the NEXT one — the agent computes "after now", and an inclusive
        // answer would re-publish the occurrence that is firing right now.
        var next = schedule.NextOccurrence(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero));

        next.ShouldBe(new DateTimeOffset(2026, 1, 16, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void computes_in_the_supplied_time_zone()
    {
        // Mid-January: Chicago is CST (UTC-6), no DST anywhere near.
        var schedule = new CronSchedule("0 9 * * *", Chicago);

        var next = schedule.NextOccurrence(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

        next.ShouldBe(new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void a_six_field_expression_carries_seconds()
    {
        var schedule = new CronSchedule("30 0 9 * * *");

        var next = schedule.NextOccurrence(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

        next.ShouldBe(new DateTimeOffset(2026, 1, 15, 9, 0, 30, TimeSpan.Zero));
    }

    [Fact]
    public void spring_forward_a_job_in_the_skipped_hour_fires_at_the_adjusted_instant()
    {
        // 2026-03-08: Chicago springs forward at 02:00 CST -> 03:00 CDT, so 02:30 does not exist.
        // Cronos fires the job at the moment the transition completes rather than dropping it:
        // 03:00 CDT = 08:00 UTC.
        var schedule = new CronSchedule("30 2 * * *", Chicago);

        var next = schedule.NextOccurrence(new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero));

        next.ShouldBe(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void fall_back_a_job_in_the_repeated_hour_fires_once_not_twice()
    {
        // 2026-11-01: Chicago falls back at 02:00 CDT -> 01:00 CST, so 01:30 local happens twice
        // (06:30 UTC as CDT, then 07:30 UTC as CST). The job fires for the FIRST and the next
        // occurrence is the following day — never the repeat.
        var schedule = new CronSchedule("30 1 * * *", Chicago);

        var first = schedule.NextOccurrence(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        first.ShouldBe(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));

        var second = schedule.NextOccurrence(first!.Value);
        second.ShouldBe(new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void an_invalid_expression_throws_at_the_construction_site()
    {
        Should.Throw<ArgumentException>(() => new CronSchedule("not cron at all"))
            .Message.ShouldContain("not cron at all");

        Should.Throw<ArgumentException>(() => new CronSchedule(" "));
    }

    [Fact]
    public void an_unsatisfiably_fast_cadence_is_refused()
    {
        // Every second can never be honoured through the durable path (the poller replays on a 5s
        // cadence), so it is refused at the line that wrote it rather than delivered late forever.
        Should.Throw<ArgumentException>(() => new CronSchedule("* * * * * *"))
            .Message.ShouldContain("cannot be honoured");
    }

    [Fact]
    public void try_parse_mirrors_the_constructor()
    {
        CronSchedule.TryParse("0 9 * * *", null, out var schedule).ShouldBeTrue();
        schedule.Expression.ShouldBe("0 9 * * *");

        CronSchedule.TryParse("nope", null, out _).ShouldBeFalse();
        CronSchedule.TryParse("* * * * * *", null, out _).ShouldBeFalse();
    }

    [Fact]
    public void the_default_struct_value_fails_loudly_not_with_a_null_reference()
    {
        // default(CronSchedule) always exists — the value-type analogue of a null reference.
        Should.Throw<InvalidOperationException>(() => default(CronSchedule).NextOccurrence(DateTimeOffset.UtcNow));
        default(CronSchedule).ToString().ShouldBe("(default CronSchedule)");
    }

    [Fact]
    public void the_occurrence_deduplication_id_is_time_zone_independent()
    {
        var message = new WolverineOptions().Schedules
            .RecurringMessage<SampleRecurringMessage>("0 9 * * *", Chicago);

        // The same instant, spelled in two offsets, must produce ONE id — this is what makes a
        // failover re-publish collapse instead of double-firing.
        var asLocal = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.FromHours(-6));
        var asUtc = new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero);

        message.DeduplicationIdFor(asLocal).ShouldBe(message.DeduplicationIdFor(asUtc));
        message.DeduplicationIdFor(asUtc).ShouldStartWith("SampleRecurringMessage:");
    }
}

public class SampleRecurringMessage;
