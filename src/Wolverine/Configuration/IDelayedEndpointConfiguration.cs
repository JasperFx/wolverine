using System.Diagnostics;

namespace Wolverine.Configuration;

public interface IDelayedEndpointConfiguration
{
    void Apply();
}

// Used internally
public interface IEndpointExpression
{
    Endpoint Endpoint { get; }
}

public abstract class DelayedEndpointConfiguration<TEndpoint> : IDelayedEndpointConfiguration, IEndpointExpression where TEndpoint : Endpoint
{
    private readonly List<Action<TEndpoint>> _configurations = new();
    protected readonly TEndpoint? _endpoint;
    private readonly object _locker = new();
    private readonly Func<TEndpoint>? _source;
    private bool _haveApplied;

    protected DelayedEndpointConfiguration(TEndpoint endpoint)
    {
        _endpoint = endpoint;
        _endpoint.RegisterDelayedConfiguration(this);
    }

    protected DelayedEndpointConfiguration(Func<TEndpoint> source)
    {
        _source = source;
    }

    public Endpoint Endpoint => _endpoint;

    void IDelayedEndpointConfiguration.Apply()
    {
        if (_haveApplied)
        {
            return;
        }

        lock (_locker)
        {
            if (_haveApplied)
            {
                return;
            }

            var endpoint = _endpoint ?? _source!();

            foreach (var action in _configurations) action(endpoint);

            _haveApplied = true;

            if (_endpoint != null)
            {
                try
                {
                    _endpoint.DelayedConfiguration.Remove(this);
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Problem while trying to apply delayed configuration");
                    Debug.WriteLine(e.ToString());
                }
            }
        }
    }

    protected void add(Action<TEndpoint> action)
    {
        _configurations.Add(action);
    }
}