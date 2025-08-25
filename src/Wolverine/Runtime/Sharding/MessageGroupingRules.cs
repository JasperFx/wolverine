using JasperFx.Core;

namespace Wolverine.Runtime.Sharding;

public class MessageGroupingRules
{
    private readonly WolverineOptions _options;
    private readonly List<IGroupingRule> _rules = new();

    public MessageGroupingRules(WolverineOptions options)
    {
        _options = options;
    }

    internal bool HasAnyRules() => _rules.Any();
    
    internal List<ShardedMessageTopology> ShardedMessageTopologies { get; } = new();

    public void AddPublishingTopology(Func<WolverineOptions, MessageGroupingRules, ShardedMessageTopology> factory)
    {
        ShardedMessageTopologies.Add(factory(_options, this));
    }
    
    /// <summary>
    /// Using the global message grouping rules, "shard" message publishing between
    /// a specified number of local queues names [baseName]1, [baseName]2, etc.
    /// </summary>
    /// <param name="baseName">The prefix for all local queues in this sharded topology</param>
    /// <param name="numberOfQueues">The number of queue "slots" for the workload</param>
    /// <param name="configure">Optionally configure each local queue's behavior</param>
    public void PublishToShardedLocalMessaging(string baseName, int numberOfQueues, Action<LocalShardedMessageTopology> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        
        var topology = new LocalShardedMessageTopology(_options, baseName, numberOfQueues);
        configure(topology);
        
        topology.AssertValidity();
        
        ShardedMessageTopologies.Add(topology);
    }
    
    /// <summary>
    /// Use any known TenantId as the message GroupId
    /// </summary>
    public MessageGroupingRules ByTenantId()
    {
        _rules.Add(new TenantGroupingRule());
        return this;
    }

    /// <summary>
    /// Configure a strategy to determine the GroupId for any message that can be
    /// cast to the type "T"
    /// </summary>
    /// <param name="strategy"></param>
    /// <typeparam name="T"></typeparam>
    public MessageGroupingRules ByMessage<T>(Func<T, string> strategy)
    {
        _rules.Add(new MessageGrouping<T>(strategy));
        return this;
    }
    
    internal string? DetermineGroupId(Envelope envelope)
    {
        if (envelope.GroupId.IsNotEmpty()) return envelope.GroupId;

        foreach (var rule in _rules)
        {
            if (rule.TryFindIdentity(envelope, out var groupId))
            {
                // Might as well save it
                envelope.GroupId = groupId;
                return groupId;
            }
        }

        return null;
    }

    internal bool TryFindTopology(Type messageType, out ShardedMessageTopology? topology)
    {
        topology = ShardedMessageTopologies.FirstOrDefault(x => x.Matches(messageType));
        return topology != null;
    }
}