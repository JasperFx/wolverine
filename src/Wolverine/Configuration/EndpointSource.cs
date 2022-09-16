using System;
using System.Collections.Generic;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

// TODO -- move everything to do with tracking transports or endpoints to a new EndpointCollection

public class EndpointSource<T> where T : Endpoint
{
    private readonly T _endpoint;

    private readonly List<Action<T>> _configurations = new();

    public EndpointSource(T endpoint)
    {
        _endpoint = endpoint;
    }

    public Uri Uri => _endpoint.Uri;

    public void Configure(Action<T> configure)
    {
        _configurations.Add(configure);
    }

    public T Configure(IWolverineRuntime runtime, IEnumerable<IEndpointPolicy> policies)
    {
        foreach (var policy in policies)
        {
            policy.Apply(_endpoint, runtime);
        }

        foreach (var configuration in _configurations)
        {
            configuration(_endpoint);
        }

        return _endpoint;
    }


}

