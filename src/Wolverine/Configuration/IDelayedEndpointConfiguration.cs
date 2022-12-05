using System;
using System.Collections.Generic;

namespace Wolverine.Configuration;

public interface IDelayedEndpointConfiguration
{
    void Apply();
}

public abstract class DelayedEndpointConfiguration<TEndpoint> : IDelayedEndpointConfiguration where TEndpoint : Endpoint
{
    private readonly List<Action<TEndpoint>> _configurations = new();
    private readonly TEndpoint _queue;

    protected DelayedEndpointConfiguration(TEndpoint queue)
    {
        _queue = queue;
        _queue.RegisterDelayedConfiguration(this);
    }

    void IDelayedEndpointConfiguration.Apply()
    {
        foreach (var action in _configurations) action(_queue);

        _queue.DelayedConfiguration.Remove(this);
    }

    protected void add(Action<TEndpoint> action)
    {
        _configurations.Add(action);
    }
}