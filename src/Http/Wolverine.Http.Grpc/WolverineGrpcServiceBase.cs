namespace Wolverine.Http.Grpc;

/// <summary>
/// Optional base class for code-first gRPC services that provides a pre-wired
/// <see cref="IMessageBus"/> via constructor injection. Inherit this class to eliminate
/// constructor boilerplate when delegating gRPC calls to Wolverine handlers.
/// </summary>
/// <example>
/// <code>
/// [ServiceContract]
/// public interface IOrderService
/// {
///     Task&lt;OrderResponse&gt; PlaceOrder(PlaceOrderRequest request, CallContext context = default);
/// }
///
/// public class OrderGrpcService : WolverineGrpcServiceBase, IOrderService
/// {
///     public OrderGrpcService(IMessageBus bus) : base(bus) { }
///
///     public Task&lt;OrderResponse&gt; PlaceOrder(PlaceOrderRequest request, CallContext context = default)
///         => Bus.InvokeAsync&lt;OrderResponse&gt;(request, context.CancellationToken);
/// }
/// </code>
/// </example>
public abstract class WolverineGrpcServiceBase
{
    /// <summary>
    /// Initializes the service with the Wolverine message bus.
    /// </summary>
    protected WolverineGrpcServiceBase(IMessageBus bus)
    {
        Bus = bus;
    }

    /// <summary>
    /// The Wolverine message bus. Use this to invoke handlers or publish messages
    /// from within gRPC service methods.
    /// </summary>
    public IMessageBus Bus { get; }
}
