using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Tracking;

/// <summary>
/// GH-5664. The virtual "now" of a tracked session, which <see cref="ITrackedSession.PlayScheduledMessagesAsync(TimeSpan, TimeSpan?, bool)" />
/// moves forward. It is either a clock the application reads too (the host's registered <see cref="TimeProvider" />, or
/// one given to <see cref="TrackedSessionConfiguration.UseClock" />), so a saga and the session agree on the time, or a
/// clock of the session's own, frozen at the instant the first session started.
/// </summary>
internal sealed class TrackedClock
{
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _advanceMethods = new();

    private readonly Action<TimeSpan> _advance;

    private TrackedClock(TimeProvider provider, bool sharedWithApplication, Action<TimeSpan> advance)
    {
        Provider = provider;
        SharedWithApplication = sharedWithApplication;
        _advance = advance;
    }

    public TimeProvider Provider { get; }

    /// <summary>
    /// True when the application reads this clock as well, so an absolute scheduled time it computed is already on
    /// this clock. Otherwise an absolute time came from the real clock and is read relative to when it was scheduled.
    /// </summary>
    public bool SharedWithApplication { get; }

    public DateTimeOffset Now => Provider.GetUtcNow();

    public void AdvanceTo(DateTimeOffset instant)
    {
        var by = instant - Now;
        if (by > TimeSpan.Zero)
        {
            _advance(by);
        }
    }

    /// <summary>
    /// The host's registered <see cref="TimeProvider" /> when the session can move it, else a clock of the session's own.
    /// </summary>
    public static TrackedClock For(IServiceProvider services)
    {
        TimeProvider? registered = null;
        try
        {
            registered = services.GetService<TimeProvider>();
        }
        catch (ObjectDisposedException)
        {
            // A host that is already gone has nothing to share
        }

        if (registered != null && tryFindAdvance(registered, out var advance))
        {
            return new TrackedClock(registered, true, advance);
        }

        var own = new SessionClock(TimeProvider.System.GetUtcNow());
        return new TrackedClock(own, false, own.Advance);
    }

    public static TrackedClock Use(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!tryFindAdvance(clock, out var advance))
        {
            throw new ArgumentException(
                $"The tracked session moves its clock forward through a public Advance(TimeSpan) method, which {clock.GetType().FullName} does not have. Use a clock that can be moved, such as FakeTimeProvider from Microsoft.Extensions.TimeProvider.Testing.",
                nameof(clock));
        }

        return new TrackedClock(clock, true, advance);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Testing support only: a test clock's public Advance(TimeSpan) method is looked up on the clock the test supplied. A trimmed clock without one is treated as a clock the session cannot move.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Testing support only, as above.")]
    private static bool tryFindAdvance(TimeProvider provider, [NotNullWhen(true)] out Action<TimeSpan>? advance)
    {
        if (provider is SessionClock own)
        {
            advance = own.Advance;
            return true;
        }

        var method = _advanceMethods.GetOrAdd(provider.GetType(),
            static type => type.GetMethod("Advance", BindingFlags.Public | BindingFlags.Instance, [typeof(TimeSpan)]));

        if (method == null)
        {
            advance = null;
            return false;
        }

        advance = by => method.Invoke(provider, [by]);
        return true;
    }

    /// <summary>A clock frozen until the session moves it.</summary>
    internal sealed class SessionClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
