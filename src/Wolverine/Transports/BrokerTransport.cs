using System.Collections.Generic;
using System.Threading.Tasks;
using Oakton.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;

/// <summary>
/// Abstract base class suitable for brokered messaging infrastructure
/// </summary>
/// <typeparam name="TEndpoint"></typeparam>
public abstract class BrokerTransport<TEndpoint> : TransportBase<TEndpoint>, IBrokerTransport where TEndpoint : Endpoint, IBrokerEndpoint
{
    protected BrokerTransport(string protocol, string name) : base(protocol, name)
    {
    }

    /// <summary>
    /// Use to sanitize names for illegal characters
    /// </summary>
    /// <param name="identifier"></param>
    /// <returns></returns>
    public virtual string SanitizeIdentifier(string identifier)
    {
        return identifier;
    }
    
    /// <summary>
    /// Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    public bool AutoProvision { get; set; }

    /// <summary>
    /// Should Wolverine attempt to purge all messages out of existing or discovered queues
    /// on application start up? This can be useful for testing, and occasionally for ephemeral
    /// messages
    /// </summary>
    public bool AutoPurgeAllQueues { get; set; }


    //public abstract ValueTask ConnectAsync();
    protected virtual void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
    {
        
    }
    
    public sealed override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = new BrokerResource(this, runtime);
        return true;
    }

    public abstract ValueTask ConnectAsync(IWolverineRuntime logger);
    public abstract IEnumerable<PropertyColumn> DiagnosticColumns();

    public sealed override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        tryBuildResponseQueueEndpoint(runtime);

        await ConnectAsync(runtime);

        foreach (var endpoint in endpoints())
        {
            await endpoint.InitializeAsync(runtime.Logger);
        }
    }
}