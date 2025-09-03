using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Partitioning;

public class MessagePartitioningRules
{
    private readonly WolverineOptions _options;
    private readonly List<IGroupingRule> _rules = new();

    public MessagePartitioningRules(WolverineOptions options)
    {
        _options = options;
    }

    internal bool HasAnyRules() => _rules.Any();
    
    internal List<PartitionedMessageTopology> ShardedMessageTopologies { get; } = new();

    public void AddPublishingTopology(Func<WolverineOptions, MessagePartitioningRules, PartitionedMessageTopology> factory)
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
    public void PublishToShardedLocalMessaging(string baseName, int numberOfQueues, Action<LocalPartitionedMessageTopology> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        
        var topology = new LocalPartitionedMessageTopology(_options, baseName, numberOfQueues);
        configure(topology);
        
        topology.AssertValidity();
        
        ShardedMessageTopologies.Add(topology);
    }
    
    /// <summary>
    /// Use any known TenantId as the message GroupId
    /// </summary>
    public MessagePartitioningRules ByTenantId()
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
    public MessagePartitioningRules ByMessage<T>(Func<T, string> strategy)
    {
        _rules.Add(new MessageGrouping<T>(strategy));
        return this;
    }

    /// <summary>
    /// Add a grouping rule based on a concrete message type and the property
    /// of the message type that exposes the group id information
    /// Used extensively internally
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="messageProperty"></param>
    /// <returns></returns>
    public MessagePartitioningRules ByMessage(Type messageType, PropertyInfo messageProperty)
    {
        var grouping = _rules.OfType<ExplicitGrouping>().FirstOrDefault();
        if (grouping == null)
        {
            grouping = new ExplicitGrouping();
            _rules.Insert(0, grouping);
        }
        
        grouping.AddMessageType(messageType, messageProperty);

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

    internal bool TryFindTopology(Type messageType, out PartitionedMessageTopology? topology)
    {
        topology = ShardedMessageTopologies.FirstOrDefault(x => x.Matches(messageType));
        return topology != null;
    }
}

internal interface IGrouper
{
    Type MessageType { get; }
    string ToGroupId(object message);
}

internal class ExplicitGrouping : IGroupingRule
{
    private ImHashMap<Type, IGrouper> _groupers = ImHashMap<Type, IGrouper>.Empty;

    public bool TryFindIdentity(Envelope envelope, out string groupId)
    {
        if (_groupers.TryFind(envelope.Message.GetType(), out var grouper))
        {
            groupId = grouper.ToGroupId(envelope.Message);
            return true;
        }
        
        groupId = default;
        return false;
    }

    public void AddMessageType(Type messageType, PropertyInfo property)
    {
        _groupers = _groupers.AddOrUpdate(messageType,
            typeof(Grouper<,>).CloseAndBuildAs<IGrouper>(property, messageType, property.PropertyType));
    }
}

internal class Grouper<TConcrete, TProperty> : IGrouper
{
    private readonly Func<TConcrete, TProperty> _source;

    public Grouper(PropertyInfo groupMember)
    {
        _source = LambdaBuilder.GetProperty<TConcrete, TProperty>(groupMember);
    }

    public Type MessageType => typeof(TConcrete);
    public string ToGroupId(object message)
    {
        var raw = _source((TConcrete)message);
        
        // If it's empty, it will get randomly sorted 
        // into the partitioned slots
        return raw?.ToString() ?? string.Empty;
    }
}