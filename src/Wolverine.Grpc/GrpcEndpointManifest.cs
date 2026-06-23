using System.Reflection;
using System.ServiceModel;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     Projects <see cref="GrpcGraph"/>'s discovered proto-first and code-first service chains into the core
///     <see cref="IGrpcEndpointManifest"/> abstraction. Only unary RPCs are included — those are the methods whose
///     generated wrapper forwards the request to <c>IMessageBus.InvokeAsync</c>, so the request type is genuinely the
///     published Wolverine message. Streaming methods, hand-written and direct-mapped services are excluded from v1.
/// </summary>
internal sealed class GrpcEndpointManifest : IGrpcEndpointManifest
{
    private readonly GrpcGraph _graph;
    private readonly WolverineGrpcOptions _grpcOptions;
    private readonly object _lock = new();
    private volatile IReadOnlyList<GrpcEndpointDescriptor>? _endpoints;

    public GrpcEndpointManifest(GrpcGraph graph, WolverineGrpcOptions grpcOptions)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _grpcOptions = grpcOptions ?? throw new ArgumentNullException(nameof(grpcOptions));
    }

    public IReadOnlyList<GrpcEndpointDescriptor> Endpoints
    {
        get
        {
            if (_endpoints != null)
            {
                return _endpoints;
            }

            lock (_lock)
            {
                if (_endpoints != null)
                {
                    return _endpoints;
                }

                // Robust to startup ordering: if no chains have been built yet (e.g. MapWolverineGrpcServices
                // hasn't run), discover now so the manifest is still populated.
                if (_graph.Chains.Count == 0 && _graph.CodeFirstChains.Count == 0 &&
                    _graph.HandWrittenChains.Count == 0)
                {
                    _graph.DiscoverServices(_grpcOptions);
                }

                _endpoints = build(_graph);
                return _endpoints;
            }
        }
    }

    private static IReadOnlyList<GrpcEndpointDescriptor> build(GrpcGraph graph)
    {
        var descriptors = new List<GrpcEndpointDescriptor>();

        // Proto-first: chain.UnaryMethods already excludes streaming RPCs.
        foreach (var chain in graph.Chains)
        {
            foreach (var method in chain.UnaryMethods)
            {
                descriptors.Add(new GrpcEndpointDescriptor(
                    chain.ProtoServiceName,
                    method.Name,
                    method.GetParameters()[0].ParameterType,
                    unwrapResponse(method.ReturnType),
                    chain.StubType,
                    GrpcServiceDiscoveryMode.ProtoFirst));
            }
        }

        // Code-first: only the unary methods are bus-invoked.
        foreach (var chain in graph.CodeFirstChains)
        {
            var serviceName = serviceNameFor(chain.ServiceContractType);

            foreach (var rpc in chain.SupportedMethods.Where(m => m.Kind == CodeFirstMethodKind.Unary))
            {
                descriptors.Add(new GrpcEndpointDescriptor(
                    serviceName,
                    rpc.Method.Name,
                    rpc.Method.GetParameters()[0].ParameterType,
                    unwrapResponse(rpc.Method.ReturnType),
                    chain.ServiceContractType,
                    GrpcServiceDiscoveryMode.CodeFirst));
            }
        }

        return descriptors;
    }

    private static Type? unwrapResponse(Type returnType)
    {
        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return null;
    }

    // Best-effort wire service name for a code-first contract: the [ServiceContract] Name if set, otherwise the
    // interface's simple name with a leading 'I' stripped.
    private static string serviceNameFor(Type contractType)
    {
        var attribute = contractType.GetCustomAttribute<ServiceContractAttribute>();
        if (attribute?.Name is { Length: > 0 } name)
        {
            return name;
        }

        var simple = contractType.Name;
        if (simple.Length > 1 && simple[0] == 'I' && char.IsUpper(simple[1]))
        {
            simple = simple[1..];
        }

        return simple;
    }
}
