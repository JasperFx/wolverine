using JasperFx.Core.Reflection;

namespace Wolverine.RateLimiting;

internal sealed class RateLimitSettingsRegistry
{
    private readonly Dictionary<Type, RateLimitSettings> _messageTypeLimits = new();
    private readonly Dictionary<string, RateLimitSettings> _endpointLimits = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAny => _messageTypeLimits.Count > 0 || _endpointLimits.Count > 0;

    public void RegisterMessageType(Type messageType, RateLimitSettings settings)
    {
        _messageTypeLimits[messageType] = settings;
    }

    public void RegisterEndpoint(Uri endpoint, RateLimitSettings settings)
    {
        _endpointLimits[endpoint.ToString()] = settings;
    }

    public bool TryFindForMessageType(Type messageType, out RateLimitSettings settings)
    {
        if (_messageTypeLimits.TryGetValue(messageType, out settings))
        {
            return true;
        }

        foreach (var pair in _messageTypeLimits)
        {
            if (messageType.CanBeCastTo(pair.Key))
            {
                settings = pair.Value;
                return true;
            }
        }

        settings = null!;
        return false;
    }

    public bool TryFindForEndpoint(Uri endpoint, out RateLimitSettings settings)
    {
        return _endpointLimits.TryGetValue(endpoint.ToString(), out settings);
    }
}
