using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Base class for generated gRPC service implementations.
/// </summary>
public abstract class GrpcHandler
{
    protected readonly IWolverineRuntime _runtime;

    protected GrpcHandler(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Creates a message bus context for handling gRPC requests.
    /// </summary>
    protected IMessageBus CreateMessageBus()
    {
        return new MessageContext(_runtime);
    }
}
