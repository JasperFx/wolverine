using Wolverine.Configuration;

namespace Wolverine.ErrorHandling;

internal class FaultPublishingPolicy : IWolverinePolicy
{
    public FaultPublishingMode GlobalMode { get; set; } = FaultPublishingMode.None;
    public Dictionary<Type, FaultPublishingMode> PerTypeOverrides { get; } = new();

    public FaultPublishingMode Resolve(Type messageType)
    {
        return PerTypeOverrides.TryGetValue(messageType, out var overrideMode)
            ? overrideMode
            : GlobalMode;
    }
}
