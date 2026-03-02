using Wolverine.Shims.MassTransit;

namespace Wolverine.Runtime;

public partial class MessageBus : IPublishEndpoint, ISendEndpointProvider
{
    async Task IPublishEndpoint.Publish<T>(T message)
    {
        await PublishAsync(message);
    }

    async Task ISendEndpointProvider.Send<T>(T message)
    {
        await SendAsync(message);
    }

    async Task ISendEndpointProvider.Send<T>(T message, Uri destinationAddress)
    {
        var endpoint = EndpointFor(destinationAddress);
        await endpoint.SendAsync(message);
    }
}
