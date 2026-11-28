using JasperFx.Events.EventModeling;
using Microsoft.AspNetCore.Authorization;
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

    /// <summary>
    ///     GH-4425. Also stamped on every slice this source emits, as
    ///     <see cref="EventModelSliceDescriptor.Origin" />, so a same-rung collision with Wolverine
    ///     core's source reads as <c>event-model://wolverine-http</c> against
    ///     <c>event-model://wolverine</c> — a file to go and look at — rather than <c>Derived</c> twice.
    /// </summary>
    public static readonly Uri SourceSubject = new($"{WolverineEventModelSource.Scheme}://wolverine-http");

    public Uri Subject => SourceSubject;

    /// <summary>
    ///     GH-4147/GH-4152. Roles here are derived off compiled <see cref="HttpChain" />s, so this source
    ///     sits on the same <see cref="EventModelProvenance.Derived" /> rung as Wolverine core's
    ///     (jasperfx#703), which is what replaced the <c>services.Insert(0, ...)</c> registration hack.
    ///     Two sources on the same rung union rather than clobber, so this and
    ///     <see cref="WolverineEventModelSource" /> still both contribute to the one model per service.
    /// </summary>
    public EventModelProvenance Provenance => EventModelProvenance.Derived;

    public async Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var graph = _options.Endpoints;
        if (graph is null) return null;

        // GH-4829: the domain policies the declared models carry decide each slice's Domain
        var domains = await EventModelSliceDomains.DeclaredAssignmentsAsync(services, token).ConfigureAwait(false);

        // GH-6005: the store's tag registry, and reflection where the runtime can do it
        return Describe(_wolverineOptions, graph.Chains, domains, EventModelDerivation.For(services));
    }

    /// <summary>Describe every routed HTTP chain as an Event Model slice.</summary>
    public static EventModelDescriptor Describe(WolverineOptions options, IEnumerable<HttpChain> chains)
        => Describe(options, chains, Array.Empty<DomainAssignmentDescriptor>());

    /// <summary>
    ///     Describe every routed HTTP chain as an Event Model slice, giving each the domain that
    ///     <paramref name="domains" /> or a <see cref="DomainAttribute" /> declares for its endpoint (GH-4829).
    /// </summary>
    public static EventModelDescriptor Describe(WolverineOptions options, IEnumerable<HttpChain> chains,
        IReadOnlyList<DomainAssignmentDescriptor> domains)
        => Describe(options, chains, domains, EventModelDerivation.Default);

    /// <summary>
    ///     Describe every routed HTTP chain as an Event Model slice, with the store's tag registry, the reflected
    ///     field shapes and the documentation comments that <paramref name="derivation" /> allows (GH-6005).
    /// </summary>
    public static EventModelDescriptor Describe(WolverineOptions options, IEnumerable<HttpChain> chains,
        IReadOnlyList<DomainAssignmentDescriptor> domains, EventModelDerivation derivation)
    {
        var chainList = chains.ToList();
        var model = Describe(options.ServiceName, chainList, domains, derivation);

        var knownTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var chain in chainList)
        {
            if (chain.RequestType?.FullName is { } request) knownTypes.TryAdd(request, chain.RequestType);
            foreach (var variable in chain.Method.Creates)
            {
                if (variable.VariableType.FullName is { } fullName) knownTypes.TryAdd(fullName, variable.VariableType);
            }
        }

        // GH-3989: an endpoint publishing to a named external system puts that system on the far end
        // of the HTTP slice too; HTTP chains have no listener, so only outbound edges apply
        return WolverineEventModelSource.ApplyExternalSystems(model, options, knownTypes: knownTypes, includeInbound: false);
    }

    /// <summary>Describe every routed HTTP chain as an Event Model slice, without endpoint-derived external systems.</summary>
    public static EventModelDescriptor Describe(string serviceName, IEnumerable<HttpChain> chains)
        => Describe(serviceName, chains, Array.Empty<DomainAssignmentDescriptor>(), EventModelDerivation.Default);

    private static EventModelDescriptor Describe(string serviceName, IEnumerable<HttpChain> chains,
        IReadOnlyList<DomainAssignmentDescriptor> domains, EventModelDerivation derivation)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var aggregates = new List<AggregateDescriptor>();
        var aggregateNames = new HashSet<string>(StringComparer.Ordinal);
        var shapeTypes = new List<Type>();

        foreach (var chain in chains.Where(x => x.RoutePattern is not null)
                     .OrderBy(x => x.RoutePattern!.RawText, StringComparer.Ordinal)
                     .ThenBy(x => x.HttpMethods.OrderBy(m => m).FirstOrDefault(), StringComparer.Ordinal))
        {
            var seed = seedFor(chain);
            slices.Add(EventModelSliceDomains.ApplyDomain(EventModelRoles.Describe(chain, seed, derivation), chain.EndpointType, chain.Method.Method, domains));
            foreach (var aggregate in EventModelRoles.AggregatesFor(chain))
            {
                if (aggregateNames.Add(aggregate.Type.FullName)) aggregates.Add(aggregate);
            }

            // GH-6005: the types whose fields the model carries
            shapeTypes.AddRange(EventModelRoles.ReferencedTypes(chain, seed));
        }

        // GH-4425: stamp Origin after FinishModel, with THIS source's subject. The shared FinishModel
        // cannot do it — both sources call it, so stamping there would label these slices as core's.
        return WolverineEventModelSource.StampOrigin(
            WolverineEventModelSource.FinishModel(
                new EventModelDescriptor(serviceName, slices)
                {
                    Aggregates = aggregates,
                    // GH-6005 (jasperfx#1297, #1299): the store's tag catalogue and the reflected shapes, model level
                    Tags = EventModelTags.Catalogue(derivation.TagTypes),
                    Shapes = derivation.ReflectShapes ? EventModelShapes.For(shapeTypes, derivation) : Array.Empty<TypeShapeDescriptor>()
                }),
            SourceSubject);
    }

    /// <summary>
    ///     Describe one HTTP chain. The slice is named for the request type when there is one — the same
    ///     key a message handler for that type would use, so an endpoint and a handler for the same command
    ///     fold into one slice — else for the verb and route.
    /// </summary>
    public static EventModelSliceDescriptor ForChain(HttpChain chain)
        => ForChain(chain, EventModelDerivation.Default);

    /// <summary>
    ///     Describe one HTTP chain with the store's tag registry and the describe-time options in
    ///     <paramref name="derivation" /> (GH-6005).
    /// </summary>
    public static EventModelSliceDescriptor ForChain(HttpChain chain, EventModelDerivation derivation)
        => EventModelRoles.Describe(chain, seedFor(chain), derivation);

    private static EventModelSliceSeed seedFor(HttpChain chain)
    {
        var route = chain.RoutePattern?.RawText ?? string.Empty;
        var methods = chain.HttpMethods.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var verb = methods.FirstOrDefault() ?? "GET";
        var requestType = chain.RequestType is { } req && req != typeof(void) ? req : null;
        var resourceType = chain.ResourceType is { } res && res != typeof(void) ? res : null;

        return new EventModelSliceSeed(
            (requestType == null ? null : EventModelRoles.DisplayNameFor(requestType)) ?? $"{verb} {route}",
            TriggerKind.Http,
            // jasperfx PublisherOrigin.HttpRouteName: the endpoint's ASP.NET Core route name, when it has one
            new PublisherOrigin { HttpRoute = route, HttpMethod = verb, HttpRouteName = chain.RouteName, Label = $"{verb} {route}" },
            requestType,
            chain.EndpointType)
        {
            // the user's endpoint method, never the generated HttpHandler's Handle override
            HandlerMethod = chain.Method.Method,
            ResponseType = resourceType,
            // Wolverine.HTTP's convention: the first return value is the response body unless the
            // endpoint is [EmptyResponse] / NoContent, in which case every return value is a message
            // (or an event) — mirrors HttpGraphUsageSource's cascading-message rule
            FirstReturnValueIsResponse = !chain.NoContent && chain.Method.Creates.Any(),
            IsQuery = methods.All(m => m is "GET" or "HEAD"),
            RequiredRoles = RequiredRolesOf(chain)
        };
    }

    /// <summary>
    ///     GH-6005 (jasperfx#1298). The roles the endpoint's authorization metadata requires: every
    ///     <see cref="IAuthorizeData.Roles" /> on the built endpoint, else on the endpoint method and class
    ///     (<c>[Authorize(Roles = "a, b")]</c>), split and trimmed. A policy name is not a role and is not read
    ///     as one; an <c>[AllowAnonymous]</c> endpoint requires none.
    /// </summary>
    public static IReadOnlyList<string> RequiredRolesOf(HttpChain chain)
    {
        IEnumerable<object> metadata = chain.Endpoint?.Metadata
                                       ?? chain.Method.Method.GetCustomAttributes(true)
                                           .Concat(chain.EndpointType.GetCustomAttributes(true));
        var list = metadata.ToList();

        if (list.OfType<IAllowAnonymous>().Any()) return Array.Empty<string>();

        return list.OfType<IAuthorizeData>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Roles))
            .SelectMany(x => x.Roles!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
