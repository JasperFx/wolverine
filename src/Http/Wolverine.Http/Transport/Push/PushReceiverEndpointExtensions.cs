using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Configures one push receiver endpoint
/// </summary>
public class PushReceiverOptions
{
    /// <summary>
    ///     Skip a message whose id the durable inbox already holds, and record each acked message there as handled
    ///     so a later redelivery of it is skipped. Requires a message store. Default is false
    /// </summary>
    public bool DeduplicateWithInbox { get; set; }

    /// <summary>
    ///     The status code that acks a message. Default is 200
    /// </summary>
    public int SuccessStatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>
    ///     The status code that asks the broker to redeliver a message. Default is 500
    /// </summary>
    public int RetryStatusCode { get; set; } = StatusCodes.Status500InternalServerError;

    /// <summary>
    ///     Stamped onto every received envelope that does not name a destination of its own, and recorded as the
    ///     <c>received_at</c> of its inbox and dead letter rows. Defaults to <c>http://localhost{pattern}</c>
    /// </summary>
    public Uri? Address { get; set; }
}

public static class PushReceiverEndpointExtensions
{
    /// <summary>
    ///     GH-5016. Add an endpoint at <paramref name="pattern" /> that receives messages a broker pushes over HTTP,
    ///     such as a Pub/Sub push subscription, for <see cref="DurabilityMode.ServerlessWithOutbox" /> or any other
    ///     deployment that receives by push. <paramref name="mapper" /> turns each request into an envelope, the
    ///     handler runs inline inside the request, and every outgoing message is sent before the response. The
    ///     status code tells the broker what happened: success when the message was handled, discarded or moved to
    ///     Wolverine's dead letter storage, a failure status when the error policies asked for a retry so the broker
    ///     redelivers it with its own backoff, and 400 when the request did not carry a message at all.
    /// </summary>
    /// <remarks>
    ///     Protect this endpoint before you deploy it, for example with the push subscription's OIDC token and an
    ///     authorization policy through the returned <see cref="RouteHandlerBuilder" />.
    /// </remarks>
    public static RouteHandlerBuilder MapWolverinePushReceiver(this IEndpointRouteBuilder endpoints, string pattern,
        IPushEnvelopeMapper mapper, Action<PushReceiverOptions>? configure = null)
    {
        if (pattern.IsEmpty())
        {
            throw new ArgumentNullException(nameof(pattern), "Cannot be empty or null");
        }

        ArgumentNullException.ThrowIfNull(mapper);

        var options = new PushReceiverOptions();
        configure?.Invoke(options);
        options.Address ??= $"http://localhost{(pattern.StartsWith('/') ? pattern : "/" + pattern)}".ToUri();

        PushReceiver? receiver = null;

        return endpoints.MapPost(pattern, async (HttpContext context, IWolverineRuntime runtime) =>
        {
            // Built on first use, once the Wolverine runtime has started
            receiver ??= new PushReceiver(runtime, options.Address, options.DeduplicateWithInbox);

            Envelope? envelope;
            try
            {
                envelope = await mapper.MapAsync(context, context.RequestAborted);
            }
            catch (Exception e)
            {
                runtime.LoggerFactory.CreateLogger<PushReceiver>().LogError(e,
                    "The push envelope mapper {Mapper} failed to read a request at {Address}", mapper, options.Address);
                return Results.StatusCode(options.RetryStatusCode);
            }

            if (envelope == null)
            {
                return Results.Problem("The request does not carry a message", statusCode: StatusCodes.Status400BadRequest);
            }

            var outcome = await receiver.ReceiveAsync(envelope, context.RequestAborted);

            return Results.StatusCode(outcome == PushReceiveOutcome.Retry ? options.RetryStatusCode : options.SuccessStatusCode);
        }).ExcludeFromDescription();
    }

    /// <summary>
    ///     GH-5016. Add a push receiver endpoint at <paramref name="pattern" /> whose mapper is
    ///     <typeparamref name="TMapper" />, resolved from the container or built with the container's services. See
    ///     <see cref="MapWolverinePushReceiver(IEndpointRouteBuilder, string, IPushEnvelopeMapper, Action{PushReceiverOptions}?)" />
    /// </summary>
    public static RouteHandlerBuilder MapWolverinePushReceiver<TMapper>(this IEndpointRouteBuilder endpoints,
        string pattern, Action<PushReceiverOptions>? configure = null) where TMapper : IPushEnvelopeMapper
    {
        var mapper = ActivatorUtilities.GetServiceOrCreateInstance<TMapper>(endpoints.ServiceProvider);
        return endpoints.MapWolverinePushReceiver(pattern, mapper, configure);
    }
}
