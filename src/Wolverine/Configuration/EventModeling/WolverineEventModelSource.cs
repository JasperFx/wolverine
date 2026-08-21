using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     The Wolverine-derived <see cref="IEventModelDefinitionSource" /> (GH-3988): one slice per message
///     handler chain, with the roles <see cref="EventModelRoles" /> derives off the chain, plus the gRPC
///     trigger for any message an RPC forwards to the bus. Registered by <c>UseWolverine()</c> ahead of
///     every other source so that <see cref="EventModelDiscovery.Assemble" /> lets the derived roles win
///     over an overlay's names.
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

        void describe(HandlerChain chain)
        {
            if (chain.Handlers.Count == 0) return;
            if (chain.MessageType.IsSystemMessageType()) return;

            slices.Add(EventModelRoles.ForHandlerChain(chain));
            foreach (var aggregate in EventModelRoles.AggregatesFor(chain))
            {
                if (aggregateNames.Add(aggregate.Type.FullName)) aggregates.Add(aggregate);
            }

            foreach (var sticky in chain.ByEndpoint) describe(sticky);
        }

        foreach (var chain in options.HandlerGraph.Chains.OrderBy(x => x.MessageType.FullName, StringComparer.Ordinal))
        {
            describe(chain);
        }

        var model = new EventModelDescriptor(options.ServiceName, slices) { Aggregates = aggregates };
        model = ApplyGrpcTriggers(model, grpc);
        return FinishModel(model);
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
            var origin = new PublisherOrigin
            {
                GrpcService = endpoint.ServiceName,
                GrpcMethod = endpoint.MethodName,
                Label = $"{endpoint.ServiceName}/{endpoint.MethodName}"
            };

            var index = slices.FindIndex(x => x.CommandType?.FullName == endpoint.RequestType.FullName);
            if (index >= 0)
            {
                var existing = slices[index];
                slices[index] = existing with
                {
                    TriggerKind = TriggerKind.Grpc,
                    TriggerOrigin = existing.TriggerOrigin ?? origin
                };
            }
            else
            {
                slices.Add(new EventModelSliceDescriptor(
                    endpoint.RequestType.Name,
                    origin.Label,
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
                });
            }
        }

        return model with { Slices = slices };
    }

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
