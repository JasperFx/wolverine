namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Wolverine-backed implementation of <see cref="ITransactionalSession"/>.
/// Delegates messaging to <see cref="IMessageBus"/>.
/// Open/Commit are not supported because Wolverine handles transactions automatically.
/// </summary>
public class WolverineTransactionalSession : ITransactionalSession
{
    private readonly IMessageBus _bus;

    public WolverineTransactionalSession(IMessageBus bus)
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

    [Obsolete("Wolverine handles transactions automatically. Delete this usage.")]
    public Task Open()
    {
        throw new NotSupportedException(
            "Wolverine handles transactions automatically. Remove calls to Open().");
    }

    [Obsolete("Wolverine handles transactions automatically. Delete this usage.")]
    public Task Commit()
    {
        throw new NotSupportedException(
            "Wolverine handles transactions automatically. Remove calls to Commit().");
    }
}
