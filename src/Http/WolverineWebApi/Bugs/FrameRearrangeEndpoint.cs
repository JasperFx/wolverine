using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Middleware;
using Wolverine.Runtime;
using WolverineWebApi.Marten;

namespace WolverineWebApi.Bugs;

public static class FrameRearrangeEndpoint
{
    [WolverineGet("/frame-rearrange/{id}")]
    public static Invoice Get([Document(Required = true, MaybeSoftDeleted = false)] Invoice invoice)
    {
        return invoice;
    }
}

public static class FrameRearrangeMiddleware
{
    public static void Before(HttpContext context)
    {
        // Very simplified code, but similar to what I have in production
        var userAgents = context.Request.Headers.UserAgent;
        if (userAgents.Any(f => f?.Contains("block-me") ?? false))
        {
            throw new Exception("Blocked client");
        }
    }
    
    public class HttpPolicy : IHttpPolicy
    {
        public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
        {
            // Only apply this policy to the FrameRearrangeEndpoint, but in production it's applied to all endpoints
            foreach (var chain in chains.Where(f => f.Method.HandlerType.Name == nameof(FrameRearrangeEndpoint)))
            {
                var middlewarePolicy = new MiddlewarePolicy();
                middlewarePolicy.AddType(typeof(FrameRearrangeMiddleware));

                middlewarePolicy.Apply([chain], rules, container);
            }
        }
    }
}