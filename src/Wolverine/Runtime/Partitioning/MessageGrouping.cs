using JasperFx.Core;

namespace Wolverine.Runtime.Partitioning;

internal class MessageGrouping<T> : IGroupingRule
{
    private readonly Func<T, string> _strategy;

    public MessageGrouping(Func<T, string> strategy)
    {
        _strategy = strategy;
    }

    public bool TryFindIdentity(Envelope envelope, out string groupId)
    {
        if (envelope.Message is T message)
        {
            groupId = _strategy(message);
            return groupId.IsNotEmpty();
        }

        groupId = null!;
        return false;
    }
}