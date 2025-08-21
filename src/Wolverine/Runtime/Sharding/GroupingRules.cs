using JasperFx.Core;

namespace Wolverine.Runtime.Sharding;

public class GroupingRules
{
    private readonly List<IGroupingRule> _rules = new();

    public void ByTenantId()
    {
        _rules.Add(new TenantGroupingRule());
    }

    public void ByMessage<T>(Func<T, string> strategy)
    {
        _rules.Add(new MessageGrouping<T>(strategy));
    }
    
    public string? DetermineGroupId(Envelope envelope)
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