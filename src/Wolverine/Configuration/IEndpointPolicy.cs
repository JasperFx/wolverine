using System;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointPolicy
{
    void Apply(Endpoint endpoint, IWolverineRuntime runtime);
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
        if (endpoint is T e) _configure(e, runtime);
    }
}

