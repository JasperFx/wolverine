using Microsoft.Extensions.Hosting;

namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Wolverine-backed implementation of <see cref="IEndpointInstance"/>.
/// Delegates messaging to <see cref="IMessageBus"/> and lifecycle to <see cref="IHost"/>.
/// </summary>
public class WolverineEndpointInstance : IEndpointInstance
{
    private readonly IMessageBus _bus;
    private readonly IHost _host;

    public WolverineEndpointInstance(IMessageBus bus, IHost host)
    {
        _bus = bus;
        _host = host;
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

    public async Task Stop()
    {
        await _host.StopAsync();
    }
}
