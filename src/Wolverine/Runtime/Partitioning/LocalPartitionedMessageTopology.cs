using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Partitioning;

public class LocalPartitionedMessageTopology : PartitionedMessageTopology
{
    private PartitionSlots _listeningSlots;

    public LocalPartitionedMessageTopology(WolverineOptions options, string baseName, int numberOfEndpoints) : base(options, PartitionSlots.Five, baseName, numberOfEndpoints)
    {
        _listeningSlots = PartitionSlots.Five;
    }
    
    /// <summary>
    /// Override the maximum number of parallel messages that can be executed
    /// at one time in one of the sharded local queues. Default is 5.
    /// </summary>
    public PartitionSlots MaxDegreeOfParallelism
    {
        get => _listeningSlots;
        set
        {
            _listeningSlots = value;
            ConfigureQueues(x => x.PartitionProcessingByGroupId(value));
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