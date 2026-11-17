using ImTools;
using JasperFx;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing.Subscriptions;

/// <summary>
///     The store agnostic half of each store's <c>PublishingRelay</c> (<c>PublishEventsToWolverine()</c>):
///     the per event type publish overrides and the loop that relays a page of events to Wolverine.
///     GH-5143.
/// </summary>
internal sealed class EventRelay
{
    private ImHashMap<Type, IPublisher> _publishers = ImHashMap<Type, IPublisher>.Empty;
    private readonly Func<IEvent, IMessageBus, ValueTask> _relay;

    /// <param name="conjoinedTenancy">
    ///     True when the store keeps tenants in the rows (conjoined tenancy). Then the event's own tenant id is
    ///     the authoritative attribution and reaches the outbound envelope verbatim, including the default
    ///     tenant id, so it cannot fall back to whatever tenant the page's bus carries. Otherwise a default
    ///     tenant event is published without a tenant. See GH-2675.
    /// </param>
    public EventRelay(bool conjoinedTenancy)
    {
        // Bound once here so the per event loop needs no tenancy comparison
        _relay = conjoinedTenancy ? relayWithEventTenant : relayWithLegacyFallthrough;
    }

    public void PublishEvent<T>(Func<IEvent<T>, IMessageBus, ValueTask> publish) where T : notnull
    {
        _publishers = _publishers.AddOrUpdate(typeof(T), new LambdaPublisher<T>(publish));
    }

    public async Task RelayAsync(IReadOnlyList<IEvent> events, IMessageBus bus)
    {
        foreach (var e in events)
        {
            if (_publishers.TryFind(e.EventType, out var publisher))
            {
                await publisher.PublishAsync(e, bus).ConfigureAwait(false);
            }
            else
            {
                await _relay(e, bus).ConfigureAwait(false);
            }
        }
    }

    private static ValueTask relayWithEventTenant(IEvent e, IMessageBus bus)
        => bus.PublishAsync(e, new DeliveryOptions { TenantId = e.TenantId });

    private static ValueTask relayWithLegacyFallthrough(IEvent e, IMessageBus bus)
        => e.TenantId != StorageConstants.DefaultTenantId
            ? bus.PublishAsync(e, new DeliveryOptions { TenantId = e.TenantId })
            : bus.PublishAsync(e);

    private interface IPublisher
    {
        ValueTask PublishAsync(object e, IMessageBus bus);
    }

    private sealed class LambdaPublisher<T> : IPublisher where T : notnull
    {
        private readonly Func<IEvent<T>, IMessageBus, ValueTask> _publish;

        public LambdaPublisher(Func<IEvent<T>, IMessageBus, ValueTask> publish)
        {
            _publish = publish;
        }

        public ValueTask PublishAsync(object e, IMessageBus bus)
        {
            return e is IEvent<T> typed ? _publish(typed, bus) : new ValueTask();
        }
    }
}
