namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Implemented by <c>Wolverine.Grpc</c> to surface the application's discovered gRPC RPC endpoints — the proto-first
/// and code-first RPCs whose generated wrapper forwards a request to the Wolverine message bus — as
/// <see cref="GrpcRpcDescriptor"/> snapshots for a monitoring console (CritterWatch's gRPC Explorer).
/// </summary>
/// <remarks>
/// Pure-Wolverine hosts with no gRPC integration won't register any source, so
/// <see cref="ServiceCapabilities.GrpcEndpoints"/> stays empty. The descriptors are projected from
/// <c>IGrpcEndpointManifest</c>, which discovers services once and caches the result; this interface just exposes the
/// projected collection so <c>ServiceCapabilities.ReadFrom</c> can fold it into the snapshot without re-running
/// discovery on every emit. Mirrors <see cref="IAspNetEndpointDescriptorSource"/>.
/// </remarks>
public interface IGrpcEndpointDescriptorSource
{
    IReadOnlyList<GrpcRpcDescriptor> Endpoints { get; }
}
