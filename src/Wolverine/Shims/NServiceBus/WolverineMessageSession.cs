namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Wolverine-backed implementation of <see cref="IMessageSession"/>.
/// Delegates all operations to <see cref="IMessageBus"/>.
/// </summary>
public class WolverineMessageSession : IMessageSession
{
    private readonly IMessageBus _bus;

    public WolverineMessageSession(IMessageBus bus)
    {
        _bus = bus;
    }

    public async Task Send(object message, SendOptions? options = null)
    {
        var deliveryOptions = options?.ToDeliveryOptions();

        if (options?.Destination != null)
        {
            var endpoint = _bus.EndpointFor(options.Destination);
            await endpoint.SendAsync(message, deliveryOptions);
        }
        else
        {
            await _bus.SendAsync(message, deliveryOptions);
        }
    }

    public async Task Publish(object message, PublishOptions? options = null)
    {
        await _bus.PublishAsync(message, options?.ToDeliveryOptions());
    }
}
