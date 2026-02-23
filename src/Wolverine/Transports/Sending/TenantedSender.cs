using ImTools;
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
    public bool SupportsNativeScheduledCancellation => false;
    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        throw new InvalidOperationException($"Unknown tenant id '{_tenantId}'");
    }
}

public class TenantedSender : ISender, ISenderRequiresCallback, ISenderWithScheduledCancellation, IAsyncDisposable
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

    public void RegisterCallback(ISenderCallback senderCallback)
    {
        if (_defaultSender is ISenderRequiresCallback defaultCallback)
        {
            defaultCallback.RegisterCallback(senderCallback);
        }

        foreach (var entry in _senders.Enumerate())
        {
            if (entry.Value is ISenderRequiresCallback tenantCallback)
            {
                tenantCallback.RegisterCallback(senderCallback);
            }
        }
    }

    public bool SupportsNativeScheduledSend => _defaultSender.SupportsNativeScheduledSend;
    public bool SupportsNativeScheduledCancellation => _defaultSender?.SupportsNativeScheduledCancellation ?? false;
    public Uri Destination { get; }
    public async Task<bool> PingAsync()
    {
        var pinged = true;
        pinged = pinged && await _defaultSender.PingAsync();
        foreach (var entry in _senders.Enumerate())
        {
            pinged = pinged && await entry.Value.PingAsync();
        }

        return pinged;
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        return senderForTenantId(envelope.TenantId).SendAsync(envelope);
    }

    public ISender SenderForTenantId(string? tenantId) => senderForTenantId(tenantId!);

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

    public async Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        // This fallback path is used when the caller doesn't go through DestinationEndpoint,
        // or when tenant context isn't available. Falls back to default sender.
        if (_defaultSender is ISenderWithScheduledCancellation cancelSender)
        {
            await cancelSender.CancelScheduledMessageAsync(schedulingToken, cancellation);
        }
        else
        {
            throw new NotSupportedException(
                "The default sender for this tenanted endpoint does not support cancellation.");
        }
    }

    public void Dispose()
    {
        if (_defaultSender is ISenderRequiresCallback defaultDisposable)
        {
            defaultDisposable.Dispose();
        }

        foreach (var entry in _senders.Enumerate())
        {
            if (entry.Value is ISenderRequiresCallback tenantDisposable)
            {
                tenantDisposable.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_defaultSender is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }

        foreach (var entry in _senders.Enumerate())
        {
            if (entry.Value is IAsyncDisposable ad2)
            {
                await ad2.DisposeAsync();
            }
        }
    }
}