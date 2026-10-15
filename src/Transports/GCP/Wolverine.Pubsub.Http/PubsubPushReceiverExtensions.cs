using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Http.Transport.Push;
using Wolverine.Runtime;

namespace Wolverine.Pubsub.Http;

public static class PubsubPushReceiverExtensions
{
    /// <summary>
    ///     GH-5016. Add an endpoint at <paramref name="pattern" /> for a Google Cloud Platform Pub/Sub push subscription
    ///     to deliver messages to, for <see cref="DurabilityMode.ServerlessWithOutbox" /> or any other deployment that
    ///     receives by push. Each pushed message is mapped by the Pub/Sub endpoint whose subscription it came from (or
    ///     the one at <paramref name="endpointUri" />), the handler runs inline inside the request, and the status code
    ///     acks the message or asks Pub/Sub to redeliver it. See
    ///     <see cref="PushReceiverEndpointExtensions.MapWolverinePushReceiver(IEndpointRouteBuilder, string, IPushEnvelopeMapper, Action{PushReceiverOptions}?)" />
    /// </summary>
    /// <remarks>
    ///     Protect this endpoint before you deploy it, for example by having the push subscription send an OIDC token
    ///     and requiring an authorization policy through the returned <see cref="RouteHandlerBuilder" />.
    /// </remarks>
    public static RouteHandlerBuilder MapPubsubPushReceiver(this IEndpointRouteBuilder endpoints, string pattern,
        Action<PushReceiverOptions>? configure = null, Uri? endpointUri = null)
    {
        var mapper = new PubsubPushEnvelopeMapper(endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>())
        {
            EndpointUri = endpointUri
        };

        return endpoints.MapWolverinePushReceiver(pattern, mapper, configure);
    }
}
