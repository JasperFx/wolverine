using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Sharding;

public class LocalShardedMessageTopology : ShardedMessageTopology
{
    private ShardSlots _listeningSlots;

    public LocalShardedMessageTopology(WolverineOptions options, string baseName, int numberOfEndpoints) : base(options, ShardSlots.Five, baseName, numberOfEndpoints)
    {
        _listeningSlots = ShardSlots.Five;
    }

    // TODO -- move this up!!!
    /// <summary>
    /// Override the maximum number of parallel messages that can be executed
    /// at one time in one of the sharded local queues
    /// </summary>
    public ShardSlots MaxDegreeOfParallelism
    {
        get => _listeningSlots;
        set
        {
            _listeningSlots = value;
            ConfigureQueues(x => x.ShardListeningByGroupId(value));
        }
    }

    public void ConfigureQueues(Action<LocalQueueConfiguration> configure)
    {
        foreach (var name in _names)
        {
            configure(_options.LocalQueue(name));
        }
    }

    protected override Endpoint buildEndpoint(WolverineOptions options, string name)
    {
        return options.LocalQueue(name).As<IEndpointExpression>().Endpoint;
    }
}