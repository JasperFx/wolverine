using Cronos;

namespace Wolverine;

/// <summary>
/// A parsed, immutable cron schedule — the value type behind <c>opts.Schedules</c>. The string
/// registration overloads are sugar over this: parsing (and therefore refusal of an invalid
/// expression) happens in the constructor, so direct construction and the string forms fail at the
/// same place with the same exception.
///
/// <para>
/// Accepts the standard 5-field cron grammar, or 6 fields where the leading field is seconds.
/// Occurrences are computed in <see cref="TimeZone" /> (UTC unless one is supplied), with DST
/// handled by Cronos — a schedule inside a spring-forward gap fires at the adjusted instant, and a
/// fall-back repeat fires once.
/// </para>
///
/// <para>
/// ⚠️ Being a struct changes the null story rather than removing it: <c>default(CronSchedule)</c>
/// exists and wraps nothing. Every member guards it with a clear exception rather than a
/// <see cref="NullReferenceException" /> from the parser internals.
/// </para>
/// </summary>
public readonly struct CronSchedule : IEquatable<CronSchedule>
{
    /// <summary>
    /// The minimum spacing between occurrences a schedule may declare. Durable scheduled messages
    /// replay on <see cref="DurabilitySettings.ScheduledJobPollingTime" /> (5 seconds by default),
    /// so a faster cadence cannot be honoured through that path — the registration is refused as
    /// unsatisfiable rather than accepted and quietly delivered late.
    /// </summary>
    public static readonly TimeSpan MinimumCadence = TimeSpan.FromSeconds(5);

    private readonly CronExpression? _expression;

    /// <summary>The raw cron expression this schedule was parsed from.</summary>
    public string Expression { get; }

    /// <summary>The time zone occurrences are computed in. UTC unless one was supplied.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// Parse a cron expression (5-field, or 6-field with a leading seconds field) into a schedule.
    /// Throws <see cref="ArgumentException" /> for an invalid expression or an unsatisfiably fast
    /// cadence — a bad schedule is a programming error and should fail at the line that wrote it.
    /// </summary>
    public CronSchedule(string expression, TimeZoneInfo? timeZone = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new ArgumentException("A cron expression is required", nameof(expression));
        }

        var fieldCount = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        try
        {
            _expression = fieldCount == 6
                ? CronExpression.Parse(expression, CronFormat.IncludeSeconds)
                : CronExpression.Parse(expression);
        }
        catch (CronFormatException e)
        {
            throw new ArgumentException(
                $"'{expression}' is not a valid cron expression: {e.Message}", nameof(expression), e);
        }

        Expression = expression;
        TimeZone = timeZone ?? TimeZoneInfo.Utc;

        assertSatisfiableCadence();
    }

    /// <summary>Parse a cron expression into a schedule. Identical to the constructor.</summary>
    public static CronSchedule Parse(string expression, TimeZoneInfo? timeZone = null)
    {
        return new CronSchedule(expression, timeZone);
    }

    /// <summary>Try-parse twin of <see cref="Parse" />. False for invalid or unsatisfiable expressions.</summary>
    public static bool TryParse(string expression, TimeZoneInfo? timeZone, out CronSchedule schedule)
    {
        try
        {
            schedule = new CronSchedule(expression, timeZone);
            return true;
        }
        catch (ArgumentException)
        {
            schedule = default;
            return false;
        }
    }

    /// <summary>
    /// The next occurrence strictly after <paramref name="after" />, or null when the expression
    /// has no further occurrence (possible with fixed-date expressions).
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after)
    {
        var expression = _expression ?? throw new InvalidOperationException(
            "This is the default, uninitialized CronSchedule value. Construct one with an expression.");

        return expression.GetNextOccurrence(after, TimeZone);
    }

    private void assertSatisfiableCadence()
    {
        // Sample the first gap from a fixed anchor. Not a proof over every gap the expression can
        // produce, but it deterministically catches the every-second / every-other-second shapes
        // that can never be honoured, at the moment they are written.
        var anchor = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = NextOccurrence(anchor);
        if (first == null) return;

        var second = NextOccurrence(first.Value);
        if (second == null) return;

        if (second.Value - first.Value < MinimumCadence)
        {
            throw new ArgumentException(
                $"'{Expression}' fires more often than every {MinimumCadence.TotalSeconds:0} seconds, " +
                $"which cannot be honoured: durable scheduled messages replay on " +
                $"{nameof(DurabilitySettings.ScheduledJobPollingTime)} (default 5s), so a faster cadence " +
                "would be accepted and then quietly delivered late.");
        }
    }

    public bool Equals(CronSchedule other)
    {
        return Expression == other.Expression && Equals(TimeZone?.Id, other.TimeZone?.Id);
    }

    public override bool Equals(object? obj)
    {
        return obj is CronSchedule other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Expression, TimeZone?.Id);
    }

    // Guarded because this is a struct: default(CronSchedule).Expression is null, and a ToString()
    // that returns null breaks logging at the worst possible moment.
    public override string ToString()
    {
        return Expression == null ? "(default CronSchedule)" : $"{Expression} ({TimeZone.Id})";
    }
}
