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
                     throw new InvalidOperationException(HttpEndpoint.NoClientRegisteredMessage(_endpoint.OutboundUri));

        try
        {
            if (_endpoint.TargetsInvokeRoute)
            {
                // GH-5231. The /invoke route takes exactly one envelope and answers a batch with a 415, so a
                // buffered or durable endpoint aimed at it posts its batch one envelope at a time instead of
                // retrying the same 415 forever. A failure part way through fails the whole batch, so the
                // envelopes already delivered may be delivered again -- at least once, never lost.
                foreach (var envelope in batch.Messages)
                {
                    await client.SendAsync(_endpoint.OutboundUri, envelope, _endpoint.SerializerOptions);
                }
            }
            else
            {
                await client.SendBatchAsync(_endpoint.OutboundUri, batch);
            }
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