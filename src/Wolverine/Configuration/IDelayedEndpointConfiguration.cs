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

    public Endpoint Endpoint => _endpoint!;

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
                    // GH-4262: through the endpoint, so this shares the endpoint's lock with
                    // RegisterDelayedConfiguration and with Endpoint.Compile's snapshot.
                    _endpoint.RemoveDelayedConfiguration(this);
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
        // GH-4262. _locker, because Apply() iterates _configurations while holding it. The base
        // constructor above publishes `this` into the endpoint's DelayedConfiguration list BEFORE the
        // derived constructor body has finished calling add(), so a concurrent Endpoint.Compile can
        // legitimately pick this instance up and Apply() it while these adds are still landing. Without
        // the lock that is one List<T> being iterated and mutated at once, and the torn read surfaces as
        // a NullReferenceException on `action(endpoint)`.
        lock (_locker)
        {
            _configurations.Add(action);
        }
    }
}