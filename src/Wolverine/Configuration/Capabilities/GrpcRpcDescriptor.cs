using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Serializable, diagnostic snapshot of a single Wolverine gRPC RPC endpoint — one descriptor per RPC method whose
/// generated wrapper forwards its request to the Wolverine message bus. Surfaced through
/// <see cref="ServiceCapabilities.GrpcEndpoints"/> so a monitoring console (CritterWatch) can build a gRPC Explorer
/// and tie each RPC to the Wolverine message it publishes — the same way <see cref="AspNetEndpointDescriptor"/> and
/// <c>HttpChainDescriptor</c> surface the HTTP surface area.
/// </summary>
/// <remarks>
/// Projected from <c>IGrpcEndpointManifest</c> (the runtime view inside <c>Wolverine.Grpc</c>) by an
/// <see cref="IGrpcEndpointDescriptorSource"/>. Only proto-first and code-first services appear — hand-written and
/// direct-mapped services delegate to the user's own implementation rather than forwarding to the bus, so they have
/// no message-publishing origin to surface. Types are carried as <see cref="TypeDescriptor"/> so the descriptor stays
/// JSON-serialisable across the capability boundary.
/// </remarks>
public class GrpcRpcDescriptor : OptionsDescription
{
    // Parameterless ctor for deserialization.
    public GrpcRpcDescriptor()
    {
    }

    /// <summary>The service name — the proto service's simple name (proto-first) or the contract's service name
    /// (code-first); not necessarily package-qualified.</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>The RPC method name.</summary>
    public string MethodName { get; set; } = "";

    /// <summary>The RPC cardinality (unary / server-streaming / bidirectional-streaming).</summary>
    public GrpcRpcStreamKind StreamKind { get; set; }

    /// <summary>How the service was discovered — <see cref="GrpcServiceDiscoveryMode.ProtoFirst"/> or
    /// <see cref="GrpcServiceDiscoveryMode.CodeFirst"/>.</summary>
    public GrpcServiceDiscoveryMode Mode { get; set; }

    /// <summary>The request type — the Wolverine message this RPC forwards to the bus (the published origin). For
    /// bidirectional RPCs this is the per-item element type of the inbound request stream.</summary>
    public TypeDescriptor? RequestType { get; set; }

    /// <summary>The response type, or <c>null</c> for a method with no typed response.</summary>
    public TypeDescriptor? ResponseType { get; set; }

    /// <summary>The forwarding origin — the discovered service identity (proto-first stub or code-first contract
    /// interface) whose generated wrapper performs the bus dispatch.</summary>
    public TypeDescriptor? Origin { get; set; }

    /// <summary>
    /// The Event Modeling slice this RPC starts (GH-4000): the RPC itself as the trigger, and — because the
    /// generated wrapper forwards the request to the bus — the forwarded message's slice around it, with the
    /// handler, the aggregate(s) it decides against and the events it emits. A trigger-only slice when nothing
    /// in this process handles the forwarded message. Null only when the roles could not be read.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="MessageHandlerDescriptor.EventModel"/>, and the same slice the assembled
    /// <see cref="ServiceCapabilities.EventModel"/> carries for this RPC — derived once, by
    /// <c>WolverineEventModelSource.ForGrpcEndpoint</c>, so the two cannot disagree. Additive and nullable on
    /// the wire: a payload written before the slot existed reads back as the descriptor it always was.
    /// </remarks>
    public EventModelSliceDescriptor? EventModel { get; set; }
}
