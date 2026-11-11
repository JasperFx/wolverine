using ImTools;
using JasperFx.Core;
using JasperFx;
using JasperFx.MultiTenancy;

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
    /// pathway. This was the default in Wolverine 6.x, and
    /// <see cref="WolverineOptions.RestoreV6Defaults" /> brings it back
    /// </summary>
    FallbackToDefault,

    /// <summary>
    /// The Wolverine 7.0 default (GH-5196): a broker is a trust boundary. A message with no
    /// tenant id goes to the default broker, a message for a registered tenant goes to that
    /// tenant's broker only, and a message for a tenant id no registered tenant matches is
    /// never sent anywhere. It is moved to the dead letter storage of the main message store
    /// with an <see cref="UnknownBrokerTenantException" /> naming the tenant id, the message
    /// type and the endpoint, or, with no message store to hold it, the send throws that
    /// exception
    /// </summary>
    DeadLetterUnknownTenants
}

/// <summary>
/// GH-5196. The reason a message for a tenant id that no tenant registered with a tenanted
/// broker transport matches is dead lettered rather than sent. Derives from JasperFx's
/// <see cref="UnknownTenantIdException" /> so an existing <c>catch (UnknownTenantIdException)</c>
/// keeps catching it
/// </summary>
public class UnknownBrokerTenantException : UnknownTenantIdException
{
    public UnknownBrokerTenantException(string tenantId, string? messageType, Uri endpoint) : base(
        $"Unknown tenant id '{tenantId}' for message type '{messageType}' at endpoint {endpoint}. No tenant with this id is registered with the transport, and a broker is a trust boundary, so the message was not sent to the default broker or any other tenant's. Register the tenant, or choose TenantedIdBehavior.FallbackToDefault for the transport to send it to the default broker as Wolverine 6.x did",
        tenantId)
    {
        MessageType = messageType;
        Endpoint = endpoint;
    }

    /// <summary>
    /// The message type name of the message that was not sent
    /// </summary>
    public string? MessageType { get; }

    /// <summary>
    /// The endpoint the message was addressed to
    /// </summary>
    public Uri Endpoint { get; }
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

internal class UnknownTenantSender : ISender
{
    private readonly string _tenantId;

    public UnknownTenantSender(Uri destination, string tenantId)
    {
        _tenantId = tenantId;
        Destination = destination;
    }

    public Uri Destination { get; }
    public bool SupportsNativeScheduledSend => false;

    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        throw new UnknownBrokerTenantException(_tenantId, envelope.MessageType, Destination);
    }
}

/// <summary>
/// Routes messages to tenant-specific senders based on envelope TenantId.
///
/// IMPORTANT: This class intentionally does NOT implement ISenderRequiresCallback.
/// When it did, SendingAgent chose the sendWithCallbackHandlingAsync path which
/// assumes the inner sender calls back on success. But transport senders like
/// RabbitMqSender are simple fire-and-forget (not ISenderRequiresCallback), so
/// MarkSuccessfulAsync was never called and outbox entries were never deleted.
/// Without ISenderRequiresCallback, SendingAgent uses sendWithExplicitHandlingAsync
/// which explicitly calls MarkSuccessfulAsync after a successful send.
/// See https://github.com/JasperFx/wolverine/issues/2361
/// </summary>
public class TenantedSender : ISender, IDisposable, IAsyncDisposable, IConditionalNativeScheduling
{
    public TenantedIdBehavior TenantedIdBehavior { get; }
    private readonly ISender _defaultSender = null!;
    private ImHashMap<string, ISender> _senders = ImHashMap<string, ISender>.Empty;

    public TenantedSender(Uri destination, TenantedIdBehavior tenantedIdBehavior, ISender? defaultSender)
    {
        Destination = destination;
        TenantedIdBehavior = tenantedIdBehavior;
        _defaultSender = defaultSender!;

        if ((tenantedIdBehavior is TenantedIdBehavior.FallbackToDefault or TenantedIdBehavior.DeadLetterUnknownTenants) && _defaultSender == null)
        {
            throw new ArgumentNullException(nameof(defaultSender),
                $"A default sender is required if using the {tenantedIdBehavior} behavior");
        }
    }

    public void RegisterSender(string tenantId, ISender sender)
    {
        _senders = _senders.AddOrUpdate(tenantId, sender);
    }

    /// <summary>
    /// The sender used for the untenanted pathway, and for unknown tenant ids when the
    /// TenantedIdBehavior is FallbackToDefault
    /// </summary>
    public ISender DefaultSender => _defaultSender;

    /// <summary>
    /// All the registered tenant id / sender pairs. Diagnostic only.
    /// </summary>
    public IEnumerable<KeyValuePair<string, ISender>> TenantSenders()
    {
        return _senders.Enumerate().Select(x => new KeyValuePair<string, ISender>(x.Key, x.Value));
    }

    public bool SupportsNativeScheduledSend => _defaultSender.SupportsNativeScheduledSend;

    bool IConditionalNativeScheduling.CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
    {
        // Tenant senders are siblings of the default sender against the same queue/topic
        // topology, so the default sender's answer holds for every tenant
        return _defaultSender is not IConditionalNativeScheduling conditional ||
               conditional.CanScheduleNatively(envelope, utcNow);
    }
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
        return senderForTenantId(envelope.TenantId!).SendAsync(envelope);
    }

    /// <summary>
    ///     The sender that really sends <paramref name="envelope" />, the one for its tenant
    /// </summary>
    internal ISender SenderFor(Envelope envelope) => senderForTenantId(envelope.TenantId!);

    /// <summary>
    ///     GH-5196. True when <paramref name="envelope" /> carries a tenant id that no registered tenant
    ///     matches and this sender's behavior is <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" />,
    ///     so it must be dead lettered rather than sent. Tenant ids are matched exactly (ordinal, case
    ///     sensitive, no trimming), the same rule a tenant-aware listener uses to stamp its tenant id
    /// </summary>
    public bool IsUnknownTenantToDeadLetter(Envelope envelope)
    {
        if (TenantedIdBehavior != TenantedIdBehavior.DeadLetterUnknownTenants) return false;
        if (isUntenanted(envelope.TenantId)) return false;

        return !_senders.TryFind(envelope.TenantId!, out _);
    }

    // The default tenant's ids mean "no tenant" here: a message from a default-tenant session goes to the default
    // broker, as one with no tenant id does
    private static bool isUntenanted(string? tenantId)
    {
        return tenantId.IsEmpty() || tenantId == StorageConstants.DefaultTenantId ||
               tenantId == TransportConstants.Default;
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
                case TenantedIdBehavior.DeadLetterUnknownTenants:
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

            case TenantedIdBehavior.DeadLetterUnknownTenants:
                if (isUntenanted(tenantId)) return _defaultSender;

                // The sending agents divert these to dead letter storage before they get this far
                // (UnknownBrokerTenants). Reaching here means a caller sent straight through the
                // sender, so refuse rather than fall back. Not cached, so a tenant registered later
                // is still found
                return new UnknownTenantSender(Destination, tenantId);
        }

        return _defaultSender;
    }

    public void Dispose()
    {
        if (_defaultSender is IDisposable defaultDisposable)
        {
            defaultDisposable.SafeDispose();
        }

        foreach (var entry in _senders.Enumerate())
        {
            if (entry.Value is IDisposable tenantDisposable)
            {
                tenantDisposable.SafeDispose();
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