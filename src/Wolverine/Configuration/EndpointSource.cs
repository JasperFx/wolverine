using System;
using System.Collections.Generic;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointSource
{
    Uri Uri { get; }
    Endpoint Build(IWolverineRuntime runtime);
}

public class EndpointSource<T> : IEndpointSource where T : Endpoint
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
    
    public Endpoint Build(IWolverineRuntime runtime)
    {
        foreach (var configuration in _configurations)
        {
            configuration(_endpoint);
        }

        return _endpoint;
    }
}

