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