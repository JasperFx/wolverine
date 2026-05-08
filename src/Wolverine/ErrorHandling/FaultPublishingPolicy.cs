using System.Collections.Frozen;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Resolved fault-publishing settings for a single message type. Held as
/// the per-type override value and returned from <see cref="FaultPublishingPolicy.Resolve"/>.
/// A per-type override is fully specified — mode and redaction never partially
/// inherit from globals.
/// </summary>
internal readonly record struct FaultPublishingDecision(
    FaultPublishingMode Mode,
    bool IncludeExceptionMessage,
    bool IncludeStackTrace);

internal sealed class FaultPublishingPolicy
{
    private readonly Dictionary<Type, FaultPublishingDecision> _builderOverrides = new();
    private FrozenDictionary<Type, FaultPublishingDecision>? _frozenOverrides;
    private bool _frozen;

    public FaultPublishingMode GlobalMode { get; set; } = FaultPublishingMode.None;
    public bool GlobalIncludeExceptionMessage { get; set; } = true;
    public bool GlobalIncludeStackTrace { get; set; } = true;

    public void SetOverride(
        Type messageType,
        FaultPublishingMode mode,
        bool includeExceptionMessage = true,
        bool includeStackTrace = true)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "FaultPublishingPolicy is frozen — per-type overrides must be configured " +
                "before WolverineRuntime starts. Move the call inside the host's UseWolverine " +
                "configuration callback.");
        }

        _builderOverrides[messageType] = new FaultPublishingDecision(
            mode, includeExceptionMessage, includeStackTrace);
    }

    /// <summary>
    /// Mark the policy read-only. Snapshots the per-type overrides into a
    /// FrozenDictionary so post-Freeze reads on the failure path observe the
    /// pre-Freeze writes via an explicit memory barrier rather than relying on
    /// implicit host-startup synchronization.
    /// </summary>
    public void Freeze()
    {
        // Snapshot first, flag second. Volatile.Write provides the release fence
        // matched by the Volatile.Read in Resolve.
        Volatile.Write(ref _frozenOverrides, _builderOverrides.ToFrozenDictionary());
        _frozen = true;
    }

    public FaultPublishingDecision Resolve(Type messageType)
    {
        var snapshot = Volatile.Read(ref _frozenOverrides);
        if (snapshot is not null)
        {
            if (snapshot.TryGetValue(messageType, out var frozen))
            {
                return frozen;
            }
        }
        else if (_builderOverrides.TryGetValue(messageType, out var builder))
        {
            // Pre-Freeze single-threaded bootstrap path — same thread that
            // wrote via SetOverride is reading here. No memory barrier needed.
            return builder;
        }

        return new FaultPublishingDecision(
            GlobalMode, GlobalIncludeExceptionMessage, GlobalIncludeStackTrace);
    }
}
