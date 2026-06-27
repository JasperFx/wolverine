using JasperFx.Descriptors;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;

namespace Wolverine.Grpc;

/// <summary>
///     Projects the runtime <see cref="IGrpcEndpointManifest"/> (discovered proto-first + code-first RPCs that
///     forward a request to the message bus) into the serializable <see cref="GrpcRpcDescriptor"/> capability shape so
///     <c>ServiceCapabilities.ReadFrom</c> can fold the gRPC surface into its snapshot for CritterWatch's gRPC
///     Explorer. Mirrors <c>Wolverine.CritterWatch.Http</c>'s <c>AspNetEndpointDiscovery</c>. The manifest discovers
///     services once and caches; this source projects on first read and caches the result.
/// </summary>
internal sealed class GrpcEndpointDescriptorSource : IGrpcEndpointDescriptorSource
{
    private readonly IGrpcEndpointManifest _manifest;
    private readonly object _lock = new();
    private volatile IReadOnlyList<GrpcRpcDescriptor>? _endpoints;

    public GrpcEndpointDescriptorSource(IGrpcEndpointManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
    }

    public IReadOnlyList<GrpcRpcDescriptor> Endpoints
    {
        get
        {
            if (_endpoints != null)
            {
                return _endpoints;
            }

            lock (_lock)
            {
                return _endpoints ??= _manifest.Endpoints.Select(toDescriptor).ToArray();
            }
        }
    }

    private static GrpcRpcDescriptor toDescriptor(GrpcEndpointDescriptor entry)
    {
        var descriptor = new GrpcRpcDescriptor
        {
            Subject = $"GrpcEndpoint[{entry.ServiceName}/{entry.MethodName}]",
            ServiceName = entry.ServiceName,
            MethodName = entry.MethodName,
            StreamKind = entry.StreamKind,
            Mode = entry.Mode,
            RequestType = TypeDescriptor.For(entry.RequestType),
            ResponseType = entry.ResponseType != null ? TypeDescriptor.For(entry.ResponseType) : null,
            Origin = TypeDescriptor.For(entry.HandlerType)
        };

        descriptor.AddTag("grpc");

        return descriptor;
    }
}
