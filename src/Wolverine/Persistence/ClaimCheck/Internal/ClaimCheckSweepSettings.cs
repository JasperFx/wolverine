namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// The resolved sweep configuration handed to <see cref="ClaimCheckSweeper"/> through DI. Registered by
/// <see cref="WolverineOptionsClaimCheckExtensions.UseClaimCheck"/> only when a payload time to live was
/// configured, so the presence of this service is itself the "sweeping is on" switch. See GH-3509.
/// </summary>
internal sealed record ClaimCheckSweepSettings(
    ClaimCheckStoreRouter Router,
    TimeSpan TimeToLive,
    TimeSpan Interval,
    int BatchSize);
