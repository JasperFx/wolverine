namespace Wolverine.ErrorHandling;

internal sealed class FaultPublishingPolicy
{
    private readonly Dictionary<Type, FaultPublishingMode> _perTypeOverrides = new();

    public FaultPublishingMode GlobalMode { get; set; } = FaultPublishingMode.None;

    public void SetOverride(Type messageType, FaultPublishingMode mode)
        => _perTypeOverrides[messageType] = mode;

    public FaultPublishingMode Resolve(Type messageType)
        => _perTypeOverrides.TryGetValue(messageType, out var overrideMode)
            ? overrideMode
            : GlobalMode;
}
