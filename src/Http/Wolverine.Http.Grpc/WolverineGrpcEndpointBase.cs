using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Base class for Wolverine-aware gRPC service endpoint implementations.
/// Provides access to the Wolverine message bus to delegate work to Wolverine handlers.
/// </summary>
/// <example>
/// <code>
/// [ServiceContract]
/// public interface IPingService
/// {
///     [OperationContract]
///     Task&lt;PongReply&gt; SendPingAsync(PingRequest request, CallContext context = default);
/// }
///
/// [WolverineGrpcService]
/// public class PingGrpcEndpoint : WolverineGrpcEndpointBase, IPingService
/// {
///     public Task&lt;PongReply&gt; SendPingAsync(PingRequest request, CallContext context = default)
///         =&gt; Bus.InvokeAsync&lt;PongReply&gt;(request, context.CancellationToken);
/// }
/// </code>
/// </example>
public abstract class WolverineGrpcEndpointBase
{
    /// <summary>
    /// Gets the Wolverine message bus for dispatching commands and queries to registered handlers.
    /// This is set by the dependency injection container when the service is resolved.
    /// </summary>
    public IMessageBus Bus { get; set; } = null!;
}
