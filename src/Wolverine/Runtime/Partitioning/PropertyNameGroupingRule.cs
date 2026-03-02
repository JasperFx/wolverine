using ImTools;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// A built-in IGroupingRule that determines the GroupId by looking for a named property
/// on the message type. Useful when you cannot add a marker interface to your message contracts
/// (e.g. auto-generated from .proto files) but still want partitioned sequential processing.
/// </summary>
internal class PropertyNameGroupingRule : IGroupingRule
{
    private readonly string[] _propertyNames;
    private ImHashMap<Type, IGrouper?> _groupers = ImHashMap<Type, IGrouper?>.Empty;

    public PropertyNameGroupingRule(string[] propertyNames)
    {
        _propertyNames = propertyNames;
    }

    public bool TryFindIdentity(Envelope envelope, out string groupId)
    {
        var messageType = envelope.Message!.GetType();

        if (!_groupers.TryFind(messageType, out var grouper))
        {
            grouper = TryBuildGrouper(messageType);
            _groupers = _groupers.AddOrUpdate(messageType, grouper);
        }

        if (grouper != null)
        {
            groupId = grouper.ToGroupId(envelope.Message);
            return true;
        }

        groupId = default!;
        return false;
    }

    private IGrouper? TryBuildGrouper(Type messageType)
    {
        foreach (var propertyName in _propertyNames)
        {
            var property = messageType.GetProperty(propertyName);
            if (property != null)
            {
                return typeof(Grouper<,>).CloseAndBuildAs<IGrouper>(property, messageType, property.PropertyType);
            }
        }

        return null;
    }
}
