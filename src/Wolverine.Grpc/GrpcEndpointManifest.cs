using System.Reflection;
using System.ServiceModel;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

/// <summary>
///     Projects <see cref="GrpcGraph"/>'s discovered proto-first and code-first service chains into the core
///     <see cref="IGrpcEndpointManifest"/> abstraction. Every RPC whose generated wrapper forwards the request to the
///     message bus is included — unary (via <c>IMessageBus.InvokeAsync</c>) plus server- and bidirectional-streaming
///     (via <c>IMessageBus.StreamAsync</c>) — so the request type is genuinely the published Wolverine message
///     (GH-3265). Hand-written and direct-mapped services are excluded: Wolverine delegates those to the user's own
///     implementation rather than forwarding to the bus, so there is no reliable message-publishing origin to surface.
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

        // Proto-first: every RPC kind Wolverine forwards to the bus. The chain pre-classifies its methods into
        // unary / server-streaming / bidirectional-streaming lists (client-streaming is rejected at construction,
        // so it never reaches here).
        foreach (var chain in graph.Chains)
        {
            // Unary: Task<TResponse> Name(TRequest, ServerCallContext) → InvokeAsync(request).
            foreach (var method in chain.UnaryMethods)
            {
                var p = method.GetParameters();
                descriptors.Add(new GrpcEndpointDescriptor(
                    chain.ProtoServiceName,
                    method.Name,
                    p[0].ParameterType,
                    genericArgument(method.ReturnType),
                    chain.StubType,
                    GrpcServiceDiscoveryMode.ProtoFirst,
                    GrpcRpcStreamKind.Unary));
            }

            // Server-streaming: Task Name(TRequest, IServerStreamWriter<TResponse>, ServerCallContext) → StreamAsync(request).
            foreach (var method in chain.ServerStreamingMethods)
            {
                var p = method.GetParameters();
                descriptors.Add(new GrpcEndpointDescriptor(
                    chain.ProtoServiceName,
                    method.Name,
                    p[0].ParameterType,
                    genericArgument(p[1].ParameterType),
                    chain.StubType,
                    GrpcServiceDiscoveryMode.ProtoFirst,
                    GrpcRpcStreamKind.ServerStreaming));
            }

            // Bidirectional: Task Name(IAsyncStreamReader<TRequest>, IServerStreamWriter<TResponse>, ServerCallContext).
            // Each inbound item is forwarded individually via StreamAsync, so the published message is the per-item
            // element type of the request stream — not the IAsyncStreamReader wrapper.
            foreach (var method in chain.BidirectionalStreamingMethods)
            {
                var p = method.GetParameters();
                var requestType = genericArgument(p[0].ParameterType);
                if (requestType == null) continue; // defensive: a bidi reader always has an element type

                descriptors.Add(new GrpcEndpointDescriptor(
                    chain.ProtoServiceName,
                    method.Name,
                    requestType,
                    genericArgument(p[1].ParameterType),
                    chain.StubType,
                    GrpcServiceDiscoveryMode.ProtoFirst,
                    GrpcRpcStreamKind.BidirectionalStreaming));
            }
        }

        // Code-first: unary and server-streaming are bus-forwarded (no bidi shape in the code-first model).
        foreach (var chain in graph.CodeFirstChains)
        {
            var serviceName = serviceNameFor(chain.ServiceContractType);

            foreach (var rpc in chain.SupportedMethods)
            {
                // Unary returns Task<TResponse>; server-streaming returns IAsyncEnumerable<TResponse>. Either way the
                // response type is the single generic argument of the return type, and the request is the first param.
                var streamKind = rpc.Kind == CodeFirstMethodKind.ServerStreaming
                    ? GrpcRpcStreamKind.ServerStreaming
                    : GrpcRpcStreamKind.Unary;

                descriptors.Add(new GrpcEndpointDescriptor(
                    serviceName,
                    rpc.Method.Name,
                    rpc.Method.GetParameters()[0].ParameterType,
                    genericArgument(rpc.Method.ReturnType),
                    chain.ServiceContractType,
                    GrpcServiceDiscoveryMode.CodeFirst,
                    streamKind));
            }
        }

        return descriptors;
    }

    /// <summary>
    ///     The single generic type argument of <paramref name="type"/> — used to unwrap <c>Task&lt;T&gt;</c>,
    ///     <c>IAsyncEnumerable&lt;T&gt;</c>, <c>IServerStreamWriter&lt;T&gt;</c>, and <c>IAsyncStreamReader&lt;T&gt;</c>
    ///     to their payload type. Returns <c>null</c> for a non-generic type (e.g. a bare <c>Task</c>).
    /// </summary>
    private static Type? genericArgument(Type type)
        => type.IsGenericType ? type.GetGenericArguments()[0] : null;

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
