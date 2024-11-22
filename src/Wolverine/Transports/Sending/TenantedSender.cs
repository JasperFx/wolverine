using JasperFx.Core;

namespace Wolverine.Transports.Sending;

public enum TenantedIdBehavior
{
    /// <summary>
    /// Wolverine will throw exceptions disallowing any messages sent with a null
    /// or empty TenantId
    /// </summary>
    TenantIdRequired,
    
    /// <summary>
    /// Wolverine will happily ignore messages sent to unknown tenants
    /// </summary>
    IgnoreUnknownTenants,
    
    /// <summary>
    /// Messages to an unknown tenant id will use a supplied default
    /// pathway
    /// </summary>
    FallbackToDefault
}

internal class InvalidTenantSender : ISender
{
    private readonly string _tenantId;
    public Uri Destination { get; }

    public InvalidTenantSender(Uri destination, string tenantId)
    {
        _tenantId = tenantId;
        Destination = destination;
    }

    public bool SupportsNativeScheduledSend => false;
    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        throw new InvalidOperationException($"Unknown tenant id '{_tenantId}'");
    }
}

public class TenantedSender : ISender, IAsyncDisposable
{
    public TenantedIdBehavior TenantedIdBehavior { get; }
    private readonly ISender _defaultSender;
    private ImHashMap<string, ISender> _senders = ImHashMap<string, ISender>.Empty;

    public TenantedSender(Uri destination, TenantedIdBehavior tenantedIdBehavior, ISender? defaultSender)
    {
        Destination = destination;
        TenantedIdBehavior = tenantedIdBehavior;
        _defaultSender = defaultSender;

        if (tenantedIdBehavior == TenantedIdBehavior.FallbackToDefault && _defaultSender == null)
        {
            throw new ArgumentNullException(nameof(defaultSender),
                "A default sender is required if using the FallbackToDefault behavior");
        }
    }

    public void RegisterSender(string tenantId, ISender sender)
    {
        _senders = _senders.AddOrUpdate(tenantId, sender);
    }

    public bool SupportsNativeScheduledSend => _defaultSender.SupportsNativeScheduledSend;
    public Uri Destination { get; }
    public async Task<bool> PingAsync()
    {
        var pinged = true;
        pinged = pinged && await _defaultSender.PingAsync();
        foreach (var sender in _senders.Enumerate().Select(x => x.Value))
        {
            pinged = pinged && await sender.PingAsync();
        }

        return pinged;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        return senderForTenantId(envelope.TenantId).SendAsync(envelope);
    }

    private ISender senderForTenantId(string tenantId)
    {
        if (tenantId.IsEmpty())
        {
            switch (TenantedIdBehavior)
            {
                case TenantedIdBehavior.TenantIdRequired:
                    throw new ArgumentNullException(nameof(tenantId));
                case TenantedIdBehavior.FallbackToDefault:
                    return _defaultSender;
            }
        }

        if (_senders.TryFind(tenantId, out var sender))
        {
            return sender;
        }

        switch (TenantedIdBehavior)
        {
            case TenantedIdBehavior.FallbackToDefault:
                _senders = _senders.AddOrUpdate(tenantId, _defaultSender);
                return _defaultSender;
            
            case TenantedIdBehavior.IgnoreUnknownTenants:
                return new NullSender(Destination);
            
            case TenantedIdBehavior.TenantIdRequired:
                var invalid = new InvalidTenantSender(Destination, tenantId);
                _senders = _senders.AddOrUpdate(tenantId, invalid);
                return invalid;
        }

        return _defaultSender;
    }

    public async ValueTask DisposeAsync()
    {
        if (_defaultSender is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }

        foreach (var sender in _senders.Enumerate().Select(x => x.Value).OfType<IAsyncDisposable>())
        {
            await sender.DisposeAsync();
        }
    }
}