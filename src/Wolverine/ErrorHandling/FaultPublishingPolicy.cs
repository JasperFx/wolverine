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
    private readonly Dictionary<Type, FaultPublishingDecision> _perTypeOverrides = new();
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

        _perTypeOverrides[messageType] = new FaultPublishingDecision(
            mode, includeExceptionMessage, includeStackTrace);
    }

    /// <summary>
    /// Mark the policy read-only. Called once during runtime startup so per-type
    /// overrides cannot be silently mutated from message handler code at runtime.
    /// </summary>
    public void Freeze() => _frozen = true;

    public FaultPublishingDecision Resolve(Type messageType)
        => _perTypeOverrides.TryGetValue(messageType, out var ov)
            ? ov
            : new FaultPublishingDecision(
                GlobalMode, GlobalIncludeExceptionMessage, GlobalIncludeStackTrace);
}
