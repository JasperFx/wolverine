using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Http.Transport;

internal class InlineHttpSender(HttpEndpoint endpoint, IWolverineRuntime runtime, IServiceProvider services) : ISender
{
    public async ValueTask SendAsync(Envelope envelope)
    {
        try
        {
            using var scope = services.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IWolverineHttpTransportClient>() ??
                         throw new InvalidOperationException("IWolverineHttpTransportClient is not registered in the service container");
            await client.SendAsync(endpoint.OutboundUri, envelope, endpoint.SerializerOptions);
        }
        catch (Exception ex)
        {
            var logger = runtime.LoggerFactory.CreateLogger<InlineHttpSender>();
            logger.LogError(
                ex,
                "Failed to send message {MessageId} to {Uri}",
                envelope.Id,
                endpoint.OutboundUri);
        }
    }

    public bool SupportsNativeScheduledSend => endpoint.SupportsNativeScheduledSend;
    public bool SupportsNativeScheduledCancellation => false;
    public Uri Destination => endpoint.Uri;
    public Task<bool> PingAsync() => Task.FromResult(true);

}