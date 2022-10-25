using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public interface IBrokerTransport<TEndpoint> where TEndpoint : Endpoint
{
    /// <summary>
    /// Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    bool AutoProvision { get; set; }

    /// <summary>
    /// Should Wolverine attempt to purge all messages out of existing or discovered queues
    /// on application start up? This can be useful for testing, and occasionally for ephemeral
    /// messages
    /// </summary>
    bool AutoPurgeAllQueues { get; set; }

    string Name { get; }
    string Protocol { get; }
    IEnumerable<Endpoint> Endpoints();
}

public interface IBrokerEndpoint
{
    ValueTask<bool> CheckAsync();
    ValueTask TeardownAsync(ILogger logger);
    ValueTask SetupAsync(ILogger logger);
}

public interface IBrokerQueue : IBrokerEndpoint
{
    ValueTask PurgeAsync(ILogger logger);
    ValueTask<Dictionary<string, object>> GetAttributesAsync();
}

/// <summary>
/// Abstract base class suitable for brokered messaging infrastructure
/// </summary>
/// <typeparam name="TEndpoint"></typeparam>
public abstract class BrokerTransport<TEndpoint> : TransportBase<TEndpoint>, IBrokerTransport<TEndpoint> where TEndpoint : Endpoint
{
    protected BrokerTransport(string protocol, string name) : base(protocol, name)
    {
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

    public abstract ValueTask ConnectAsync(IWolverineRuntime logger);
    
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