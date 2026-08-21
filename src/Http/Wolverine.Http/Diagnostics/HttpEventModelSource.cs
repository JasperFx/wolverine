using JasperFx.Events.EventModeling;
using Wolverine.Configuration.EventModeling;

namespace Wolverine.Http.Diagnostics;

/// <summary>
///     The Wolverine.HTTP-derived <see cref="IEventModelDefinitionSource" /> (GH-3988): one Event Model
///     slice per <see cref="HttpChain" />, triggered by the route and verb, with the command being the
///     request body and the roles derived off the endpoint signature by <see cref="EventModelRoles" />
///     exactly as for a message handler chain. Contributes to the same model as Wolverine core's source —
///     named for the service — so the assembled picture is one model per service.
/// </summary>
internal sealed class HttpEventModelSource : IEventModelDefinitionSource
{
    private readonly WolverineHttpOptions _options;
    private readonly WolverineOptions _wolverineOptions;

    public HttpEventModelSource(WolverineHttpOptions options, WolverineOptions wolverineOptions)
    {
        _options = options;
        _wolverineOptions = wolverineOptions;
    }

    public Uri Subject { get; } = new($"{WolverineEventModelSource.Scheme}://wolverine-http");

    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var graph = _options.Endpoints;
        if (graph is null) return Task.FromResult<EventModelDescriptor?>(null);

        return Task.FromResult<EventModelDescriptor?>(Describe(_wolverineOptions.ServiceName, graph.Chains));
    }

    /// <summary>Describe every routed HTTP chain as an Event Model slice.</summary>
    public static EventModelDescriptor Describe(string serviceName, IEnumerable<HttpChain> chains)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var aggregates = new List<AggregateDescriptor>();
        var aggregateNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chain in chains.Where(x => x.RoutePattern is not null)
                     .OrderBy(x => x.RoutePattern!.RawText, StringComparer.Ordinal)
                     .ThenBy(x => x.HttpMethods.OrderBy(m => m).FirstOrDefault(), StringComparer.Ordinal))
        {
            slices.Add(ForChain(chain));
            foreach (var aggregate in EventModelRoles.AggregatesFor(chain))
            {
                if (aggregateNames.Add(aggregate.Type.FullName)) aggregates.Add(aggregate);
            }
        }

        return WolverineEventModelSource.FinishModel(new EventModelDescriptor(serviceName, slices) { Aggregates = aggregates });
    }

    /// <summary>
    ///     Describe one HTTP chain. The slice is named for the request type when there is one — the same
    ///     key a message handler for that type would use, so an endpoint and a handler for the same command
    ///     fold into one slice — else for the verb and route.
    /// </summary>
    public static EventModelSliceDescriptor ForChain(HttpChain chain)
    {
        var route = chain.RoutePattern?.RawText ?? string.Empty;
        var methods = chain.HttpMethods.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var verb = methods.FirstOrDefault() ?? "GET";
        var requestType = chain.RequestType is { } req && req != typeof(void) ? req : null;
        var resourceType = chain.ResourceType is { } res && res != typeof(void) ? res : null;

        var seed = new EventModelSliceSeed(
            requestType?.Name ?? $"{verb} {route}",
            TriggerKind.Http,
            new PublisherOrigin { HttpRoute = route, HttpMethod = verb, Label = $"{verb} {route}" },
            requestType,
            chain.EndpointType)
        {
            ResponseType = resourceType,
            // Wolverine.HTTP's convention: the first return value is the response body unless the
            // endpoint is [EmptyResponse] / NoContent, in which case every return value is a message
            // (or an event) — mirrors HttpGraphUsageSource's cascading-message rule
            FirstReturnValueIsResponse = !chain.NoContent && chain.Method.Creates.Any(),
            IsQuery = methods.All(m => m is "GET" or "HEAD")
        };

        return EventModelRoles.Describe(chain, seed);
    }
}
