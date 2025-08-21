using JasperFx.Core;

namespace Wolverine.Runtime.Sharding;

public class MessageGroupingRules
{
    private readonly List<IGroupingRule> _rules = new();

    public bool HasAnyRules() => _rules.Any();
    
    /// <summary>
    /// Use any known TenantId as the message GroupId
    /// </summary>
    public void ByTenantId()
    {
        _rules.Add(new TenantGroupingRule());
    }

    /// <summary>
    /// Configure a strategy to determine the GroupId for any message that can be
    /// cast to the type "T"
    /// </summary>
    /// <param name="strategy"></param>
    /// <typeparam name="T"></typeparam>
    public void ByMessage<T>(Func<T, string> strategy)
    {
        _rules.Add(new MessageGrouping<T>(strategy));
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
}