using Microsoft.Extensions.DependencyInjection;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Http.Transport;

internal class HttpSenderProtocol : ISenderProtocol
{
    private readonly HttpEndpoint _endpoint;
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _clientFactory;

    public HttpSenderProtocol(HttpEndpoint endpoint, IServiceProvider services)
    {
        _endpoint = endpoint;
        _services = services;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        using var scope = _services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<WolverineHttpTransportClient>();
        await client.SendBatchAsync(_endpoint.OutboundUri, batch);
    }
}