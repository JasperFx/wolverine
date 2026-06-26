using Microsoft.Extensions.DependencyInjection;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Http.Transport;

internal class HttpSenderProtocol : ISenderProtocol
{
    private readonly HttpEndpoint _endpoint;
    private readonly IServiceProvider _services;

    public HttpSenderProtocol(HttpEndpoint endpoint, IServiceProvider services)
    {
        _endpoint = endpoint;
        _services = services;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        using var scope = _services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWolverineHttpTransportClient>() ??
                     throw new InvalidOperationException("IWolverineHttpTransportClient is not registered in the service container");

        try
        {
            await client.SendBatchAsync(_endpoint.OutboundUri, batch);
        }
        catch (Exception e)
        {
            // Signal failure so the durable sending agent requeues rather than dropping the batch.
            await callback.MarkProcessingFailureAsync(batch, e);
            return;
        }

        // Acknowledge success so the durable sending agent deletes the outgoing envelopes from the
        // outbox. Without this the durable HTTP transport redelivers the same messages forever (#3173).
        await callback.MarkSuccessfulAsync(batch);
    }
}