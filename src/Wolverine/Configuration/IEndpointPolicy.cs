using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointPolicy : IWolverinePolicy
{
    void Apply(Endpoint endpoint, IWolverineRuntime runtime);
}

internal class ServerlessEndpointsMustBeInlinePolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        try
        {
            // GH-3708: this silently downgrades a NativeAck endpoint to Inline, which drops its partitioning and
            // parallelism. Correct for Serverless -- there is no long-running process to hold an execution block --
            // but it should say so out loud once a transport can actually opt into NativeAck. Tracked with the
            // fluent API work rather than here, because nothing can reach this state yet.
            endpoint.Mode = EndpointMode.Inline;
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("All endpoints must be Inline when running in Serverless mode", e);
        }
    }
}

public class LambdaEndpointPolicy<T> : IEndpointPolicy where T : Endpoint
{
    private readonly Action<T, IWolverineRuntime> _configure;

    public LambdaEndpointPolicy(Action<T, IWolverineRuntime> configure)
    {
        _configure = configure;
    }

    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint is T e)
        {
            _configure(e, runtime);
        }
    }
}