using System;
using System.Collections.Generic;

namespace Wolverine.Configuration;

public interface IDelayedEndpointConfiguration
{
    void Apply();
}

public abstract class DelayedEndpointConfiguration<TEndpoint> : IDelayedEndpointConfiguration where TEndpoint : Endpoint
{
    private readonly Func<TEndpoint>? _source;
    private readonly List<Action<TEndpoint>> _configurations = new();
    private readonly TEndpoint? _endpoint;
    private readonly object _locker = new ();

    protected DelayedEndpointConfiguration(TEndpoint endpoint)
    {
        _endpoint = endpoint;
        _endpoint.RegisterDelayedConfiguration(this);
    }

    protected DelayedEndpointConfiguration(Func<TEndpoint> source)
    {
        _source = source;
    }

    void IDelayedEndpointConfiguration.Apply()
    {
        lock (_locker)
        {
            var endpoint = _endpoint ?? _source!();
        
            foreach (var action in _configurations) action(endpoint);

            if (_endpoint != null)
            {
                _endpoint.DelayedConfiguration.Remove(this);
            }
        }
    }

    protected void add(Action<TEndpoint> action)
    {
        _configurations.Add(action);
    }
}