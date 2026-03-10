using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Base class for Wolverine-aware gRPC service endpoint implementations using the
/// <strong>code-first</strong> approach (protobuf-net.Grpc).
/// Provides access to the Wolverine message bus via the <see cref="Bus"/> property,
/// which is populated by ASP.NET Core's DI container at request time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When is this base class required?</strong><br/>
/// It is required when you rely on the <em>naming-convention</em> discovery path — i.e., your
/// class name ends with <c>GrpcEndpoint</c>, <c>GrpcEndpoints</c>, <c>GrpcService</c>, or
/// <c>GrpcServices</c> without having the <see cref="WolverineGrpcServiceAttribute"/> applied.
/// </para>
/// <para>
/// <strong>When is this base class optional?</strong><br/>
/// If you apply <see cref="WolverineGrpcServiceAttribute"/> to your class, <em>this base class
/// is NOT required</em>. This is the pattern used for <strong>proto-first</strong> services
/// (Grpc.AspNetCore + <c>.proto</c> files), which must inherit the proto-generated base class
/// (e.g. <c>Greeter.GreeterBase</c>) instead. Proto-first services inject
/// <see cref="IMessageBus"/> via the constructor rather than using this <see cref="Bus"/> property.
/// </para>
/// </remarks>
/// <example>
/// <para>Code-first usage (with base class):</para>
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
