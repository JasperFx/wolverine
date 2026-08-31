using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     The Wolverine-derived <see cref="IEventModelDefinitionSource" /> (GH-3988): one slice per message
///     handler chain, with the roles <see cref="EventModelRoles" /> derives off the chain, plus the gRPC
///     trigger for any message an RPC forwards to the bus.
/// </summary>
/// <remarks>
///     HTTP chains are described by <c>Wolverine.Http</c>'s sibling source — the HTTP graph is not known to
///     Wolverine core — and both contribute to the same model, named for the service, so the
///     assembled picture has one model per service however many sources fed it.
/// </remarks>
public sealed class WolverineEventModelSource : IEventModelDefinitionSource
{
    /// <summary>URI scheme for the Wolverine-derived source: <c>event-model://wolverine/{service}</c>.</summary>
    public const string Scheme = "event-model";

    public Uri Subject { get; } = new($"{Scheme}://wolverine");

    /// <summary>
    ///     GH-4147/GH-4152. Every role this source claims is read off a compiled handler chain, so it sits
    ///     on the <see cref="EventModelProvenance.Derived" /> rung rather than the
    ///     <see cref="EventModelProvenance.Declared" /> default (jasperfx#703).
    ///
    ///     <para>This is what makes derived roles beat an overlay's. Until JasperFx 2.56 the mechanism was
    ///     registration order — <c>UseWolverine()</c> did <c>services.Insert(0, ...)</c> purely so this
    ///     source merged first — which was load-bearing behaviour that nothing in the registration
    ///     explained. Precedence is now on the ladder, so the insert is gone and the ordering no longer
    ///     matters.</para>
    ///
    ///     <para>Note the deliberate inversion: a source that <em>observes</em> a running system outranks
    ///     this one. That is the point of the ladder, not a regression — production truth beats what the
    ///     code says it should do. Precedence is also per <em>claimed</em> role, so this does not start
    ///     overwriting an overlay's slice names, domains or specification links; nothing else claims the
    ///     factual roles this source fills in.</para>
    /// </summary>
    public EventModelProvenance Provenance => EventModelProvenance.Derived;

    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        // WolverineOptions rather than IWolverineRuntime on purpose: the export command (GH-3990)
        // describes a host that was never started, and the options + a compiled handler graph are
        // all this needs. A started host has the same options, so the two paths agree.
        var options = services.GetService<WolverineOptions>();
        if (options is null) return Task.FromResult<EventModelDescriptor?>(null);

        var descriptor = Describe(options, services.GetService<IGrpcEndpointManifest>());
        return Task.FromResult<EventModelDescriptor?>(descriptor);
    }

    /// <summary>
    ///     Describe every message handler chain on the runtime's handler graph as an Event Model slice.
    /// </summary>
    public static EventModelDescriptor Describe(IWolverineRuntime runtime, IGrpcEndpointManifest? grpc = null)
        => Describe(runtime.Options, grpc);

    /// <summary>
    ///     Describe every message handler chain on the runtime's handler graph as an Event Model slice.
    ///     The model is named for the service; slices are named for their message type.
    /// </summary>
    /// <param name="options">The Wolverine options — the handler graph must have been compiled (a started host, or the code file collections resolved as the <c>event-model</c> command does).</param>
    /// <param name="grpc">The gRPC endpoint manifest, when <c>Wolverine.Grpc</c> is in play, so an RPC that forwards a message to the bus is reported as that slice's trigger.</param>
    public static EventModelDescriptor Describe(WolverineOptions options, IGrpcEndpointManifest? grpc = null)
    {
        var slices = new List<EventModelSliceDescriptor>();
        var aggregates = new List<AggregateDescriptor>();
        var aggregateNames = new HashSet<string>(StringComparer.Ordinal);
        var stickyEndpoints = new Dictionary<string, HashSet<Uri>>(StringComparer.Ordinal);
        var knownTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var chain in DescribedChains(options))
        {
            var slice = EventModelRoles.ForHandlerChain(chain);
            slices.Add(slice);

            knownTypes.TryAdd(chain.MessageType.FullName!, chain.MessageType);
            foreach (var call in chain.Handlers)
            foreach (var variable in call.Creates)
            {
                if (variable.VariableType.FullName is { } fullName) knownTypes.TryAdd(fullName, variable.VariableType);
            }

            // a sticky handler chain is bound to these listeners — that is what makes a listener the
            // trigger of this slice (GH-3989)
            if (chain.Endpoints.Count > 0)
            {
                if (!stickyEndpoints.TryGetValue(slice.Name, out var uris))
                {
                    uris = new HashSet<Uri>();
                    stickyEndpoints[slice.Name] = uris;
                }

                foreach (var endpoint in chain.Endpoints) uris.Add(endpoint.Uri);
            }
            foreach (var aggregate in EventModelRoles.AggregatesFor(chain))
            {
                if (aggregateNames.Add(aggregate.Type.FullName)) aggregates.Add(aggregate);
            }
        }

        var model = new EventModelDescriptor(options.ServiceName, slices) { Aggregates = aggregates };
        model = ApplyGrpcTriggers(model, grpc);
        model = ApplyExternalSystems(model, options, stickyEndpoints, knownTypes, includeInbound: true);
        return FinishModel(model);
    }

    /// <summary>
    ///     Every handler chain that gets a slice, in the order the model lists them: sticky per-endpoint
    ///     sub-chains ahead of the parent chain they hang off, whole graph ordered by message type. Shared
    ///     with <see cref="ForGrpcEndpoint" /> so a slice attached to one route is the same slice, chosen the
    ///     same way, as the one the assembled model carries.
    /// </summary>
    internal static IEnumerable<HandlerChain> DescribedChains(WolverineOptions options)
        => options.HandlerGraph.Chains
            .OrderBy(x => x.MessageType.FullName, StringComparer.Ordinal)
            .SelectMany(describedChains);

    private static IEnumerable<HandlerChain> describedChains(HandlerChain chain)
    {
        if (chain.MessageType.IsSystemMessageType()) yield break;

        // sticky handlers live on per-endpoint sub-chains, so walk those whether or not the
        // parent chain has a default handler of its own
        foreach (var sticky in chain.ByEndpoint)
        foreach (var described in describedChains(sticky))
        {
            yield return described;
        }

        if (chain.Handlers.Count > 0) yield return chain;
    }

    /// <summary>
    ///     GH-3989: the <em>edge</em> of a translation slice is the endpoint — a listener receiving from, or a
    ///     sender publishing to, something outside this application — and the external system's
    ///     <em>name</em> is what the application declared on that endpoint with <c>.ExternalSystem("...")</c>.
    ///     Inbound: the named listener is the trigger of every slice stuck to it, or whose command is its
    ///     <see cref="Endpoint.MessageType"/>; a listener bound to no slice still renders, as a trigger-only
    ///     translation slice. Outbound: every slice whose published messages or emitted events the named
    ///     endpoint subscribes to gets the external system on its far end.
    /// </summary>
    /// <param name="model">The model to annotate.</param>
    /// <param name="options">The Wolverine options — the endpoints come from <c>Transports.AllEndpoints()</c>.</param>
    /// <param name="stickyEndpointsBySlice">Listener URIs each slice's chain is stuck to, by slice name. May be empty.</param>
    /// <param name="knownTypes">The CLR types behind the slices' message / event descriptors, by full name, so a subscription's own matching rules decide the outbound edge. Descriptors with no known type fall back to a name match.</param>
    /// <param name="includeInbound">False for a source whose slices have no listener (Wolverine.HTTP): only outbound edges apply.</param>
    public static EventModelDescriptor ApplyExternalSystems(
        EventModelDescriptor model,
        WolverineOptions options,
        IReadOnlyDictionary<string, HashSet<Uri>>? stickyEndpointsBySlice = null,
        IReadOnlyDictionary<string, Type>? knownTypes = null,
        bool includeInbound = true)
    {
        var named = options.Transports.AllEndpoints()
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalSystemName))
            .OrderBy(x => x.Uri.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (named.Length == 0) return model;

        var slices = model.Slices.ToList();

        foreach (var endpoint in named)
        {
            var name = endpoint.ExternalSystemName!;
            var uri = endpoint.Uri.ToString();

            // Outbound: the endpoint subscribes to a message this slice publishes or an event it emits
            for (var i = 0; i < slices.Count; i++)
            {
                var slice = slices[i];
                var outgoing = slice.PublishedMessages.Concat(slice.EmittedEvents).ToArray();
                if (outgoing.Length == 0) continue;

                if (endpoint.Subscriptions.Any(subscription => outgoing.Any(type => matches(subscription, type, knownTypes))))
                {
                    slices[i] = withExternalSystem(slice, new ExternalSystemDescriptor(name, ExternalSystemDirection.Outbound, uri), flipPattern: false);
                }
            }

            if (!includeInbound || !endpoint.IsListener) continue;

            // Inbound: the listener is the trigger of a slice stuck to it, or whose command is its message type
            var matched = false;
            for (var i = 0; i < slices.Count; i++)
            {
                var slice = slices[i];
                var stuck = stickyEndpointsBySlice != null &&
                            stickyEndpointsBySlice.TryGetValue(slice.Name, out var uris) && uris.Contains(endpoint.Uri);
                var byType = endpoint.MessageType != null && slice.CommandType?.FullName == endpoint.MessageType.FullName;
                if (!stuck && !byType) continue;

                matched = true;
                var label = $"{name} → {endpoint.EndpointName}";
                slices[i] = withExternalSystem(slice, new ExternalSystemDescriptor(name, ExternalSystemDirection.Inbound, uri), flipPattern: true)
                    with
                    {
                        TriggerLabel = slice.TriggerLabel ?? label,
                        TriggerKind = TriggerKind.External,
                        TriggerOrigin = slice.TriggerOrigin ?? new PublisherOrigin { Label = label }
                    };
            }

            if (!matched)
            {
                // Nothing binds the listener to a slice — there is still a boundary to render
                slices.Add(new EventModelSliceDescriptor(
                    endpoint.EndpointName,
                    $"{name} → {endpoint.EndpointName}",
                    null,
                    endpoint.MessageType is null ? null : JasperFx.Descriptors.TypeDescriptor.For(endpoint.MessageType),
                    null,
                    Array.Empty<JasperFx.Descriptors.TypeDescriptor>(),
                    Array.Empty<JasperFx.Descriptors.TypeDescriptor>(),
                    Array.Empty<JasperFx.Descriptors.TypeDescriptor>())
                {
                    Pattern = SlicePattern.Translation,
                    TriggerKind = TriggerKind.External,
                    TriggerOrigin = new PublisherOrigin { Label = $"{name} → {endpoint.EndpointName}" },
                    ExternalSystems = new[] { new ExternalSystemDescriptor(name, ExternalSystemDirection.Inbound, uri) }
                });
            }
        }

        return model with { Slices = slices };

        static bool matches(Wolverine.Runtime.Routing.Subscription subscription, JasperFx.Descriptors.TypeDescriptor descriptor,
            IReadOnlyDictionary<string, Type>? knownTypes)
        {
            // Subscriptions match on Type; the slice carries TypeDescriptors. The source that derived the
            // slice knows the types, so the subscription's own rules decide; a descriptor nobody resolved
            // (an overlay's, say) gets the name-only approximation of the same rules.
            if (knownTypes != null && knownTypes.TryGetValue(descriptor.FullName, out var type))
            {
                return subscription.Matches(type);
            }

            return subscription.Scope switch
            {
                Wolverine.Runtime.Routing.RoutingScope.Type => descriptor.Name.Equals(subscription.Match, StringComparison.OrdinalIgnoreCase) ||
                                                               descriptor.FullName.Equals(subscription.Match, StringComparison.OrdinalIgnoreCase),
                Wolverine.Runtime.Routing.RoutingScope.TypeName => descriptor.FullName.Equals(subscription.Match, StringComparison.OrdinalIgnoreCase),
                Wolverine.Runtime.Routing.RoutingScope.Namespace => descriptor.FullName.StartsWith(subscription.Match + ".", StringComparison.OrdinalIgnoreCase),
                Wolverine.Runtime.Routing.RoutingScope.Assembly => descriptor.AssemblyName.Equals(subscription.Match, StringComparison.OrdinalIgnoreCase),
                Wolverine.Runtime.Routing.RoutingScope.Implements => false,
                _ => true,
            };
        }

        static EventModelSliceDescriptor withExternalSystem(EventModelSliceDescriptor slice, ExternalSystemDescriptor system, bool flipPattern)
        {
            if (slice.ExternalSystems.Any(x => x.Name == system.Name && x.Direction == system.Direction)) return slice;

            var systems = slice.ExternalSystems.Concat(new[] { system }).ToList();
            // An external system on the inbound side makes this a translation slice. On the outbound side
            // a slice keeps its own pattern (a command slice that also notifies Stripe is still a command
            // slice) unless it is a pure relay — no aggregate, no events of its own.
            var pattern = flipPattern || (slice.AggregateTypes.Count == 0 && slice.EmittedEvents.Count == 0 && slice.Pattern == SlicePattern.Command)
                ? SlicePattern.Translation
                : slice.Pattern;

            return slice with { ExternalSystems = systems, Pattern = pattern };
        }
    }

    /// <summary>
    ///     An RPC whose generated wrapper forwards its request to the message bus <em>is</em> the trigger of
    ///     that message's slice — stamp the gRPC trigger onto it, or add a trigger-only slice when no
    ///     handler chain exists for the request type in this process.
    /// </summary>
    internal static EventModelDescriptor ApplyGrpcTriggers(EventModelDescriptor model, IGrpcEndpointManifest? grpc)
    {
        if (grpc is null || grpc.Endpoints.Count == 0) return model;

        var slices = model.Slices.ToList();
        foreach (var endpoint in grpc.Endpoints.OrderBy(x => x.ServiceName + "::" + x.MethodName, StringComparer.Ordinal))
        {
            var origin = GrpcOriginFor(endpoint);

            var index = slices.FindIndex(x => x.CommandType?.FullName == endpoint.RequestType.FullName);
            if (index >= 0)
            {
                slices[index] = withGrpcTrigger(slices[index], origin);
            }
            else
            {
                slices.Add(grpcTriggerOnlySlice(endpoint, origin));
            }
        }

        return model with { Slices = slices };
    }

    /// <summary>
    ///     GH-4000: the Event Model slice for one gRPC RPC, so the descriptor for that RPC can carry the
    ///     slice next to the method rather than leaving a consumer to find it in the assembled model. The
    ///     RPC forwards its request to the bus, so the slice is the <em>forwarded message's</em> slice with
    ///     the RPC stamped on as its trigger — or, when nothing in this process handles that message, the
    ///     trigger-only slice the assembled model would carry for it.
    /// </summary>
    /// <param name="endpoint">The discovered RPC.</param>
    /// <param name="options">The Wolverine options, so the forwarded message's handler chain can be found. Null yields the trigger-only slice.</param>
    public static EventModelSliceDescriptor ForGrpcEndpoint(GrpcEndpointDescriptor endpoint, WolverineOptions? options)
    {
        var origin = GrpcOriginFor(endpoint);

        // the same chain, chosen the same way, that Describe() would have turned into the slice
        // ApplyGrpcTriggers then stamps: first match in model order wins
        var chain = options is null
            ? null
            : DescribedChains(options).FirstOrDefault(x => x.MessageType.FullName == endpoint.RequestType.FullName);

        return chain is null
            ? grpcTriggerOnlySlice(endpoint, origin)
            : withGrpcTrigger(EventModelRoles.ForHandlerChain(chain), origin);
    }

    internal static PublisherOrigin GrpcOriginFor(GrpcEndpointDescriptor endpoint) => new()
    {
        GrpcService = endpoint.ServiceName,
        GrpcMethod = endpoint.MethodName,
        Label = $"{endpoint.ServiceName}/{endpoint.MethodName}"
    };

    private static EventModelSliceDescriptor withGrpcTrigger(EventModelSliceDescriptor slice, PublisherOrigin origin)
        => slice with
        {
            TriggerKind = TriggerKind.Grpc,
            TriggerOrigin = slice.TriggerOrigin ?? origin
        };

    private static EventModelSliceDescriptor grpcTriggerOnlySlice(GrpcEndpointDescriptor endpoint, PublisherOrigin origin)
        => new(
            EventModelRoles.DisplayNameFor(endpoint.RequestType),
            // GH-4181: TriggerLabel unclaimed, for the same reason EventModelRoles.Describe leaves it
            // unclaimed -- this slice sets the origin two lines down, so claiming the label as well only
            // duplicated the origin onto the Derived rung and beat any label an overlay declared
            null,
            null,
            JasperFx.Descriptors.TypeDescriptor.For(endpoint.RequestType),
            null,
            Array.Empty<JasperFx.Descriptors.TypeDescriptor>(),
            Array.Empty<JasperFx.Descriptors.TypeDescriptor>(),
            Array.Empty<JasperFx.Descriptors.TypeDescriptor>())
        {
            Pattern = SlicePattern.Command,
            TriggerKind = TriggerKind.Grpc,
            TriggerOrigin = origin
        };

    /// <summary>
    ///     Model-wide derivations that need every slice at once: a slice whose command is an event some
    ///     other slice emits is an <see cref="SlicePattern.Automation" /> — the "when X, do Y" reaction — not
    ///     a command slice.
    /// </summary>
    public static EventModelDescriptor FinishModel(EventModelDescriptor model)
    {
        var emitted = new HashSet<string>(
            model.Slices.SelectMany(x => x.EmittedEvents).Select(x => x.FullName),
            StringComparer.Ordinal);

        if (emitted.Count == 0) return model;

        var slices = model.Slices
            .Select(slice => slice.CommandType is { } command && emitted.Contains(command.FullName) &&
                             slice.Pattern == SlicePattern.Command
                ? slice with { Pattern = SlicePattern.Automation }
                : slice)
            .ToList();

        return model with { Slices = slices };
    }
}
