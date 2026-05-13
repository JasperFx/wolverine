using System.Diagnostics.CodeAnalysis;
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

    // GetProperty(propertyName) (IL2070) requires PublicProperties on messageType;
    // CloseAndBuildAs over typeof(Grouper<,>) closes the partitioner generic over
    // (messageType, property.PropertyType) and trips IL2026 + IL3050. Both are
    // best-effort opt-in partitioning. AOT-clean apps using property-name
    // grouping must keep the targeted property (via [DynamicallyAccessedMembers]
    // on the message type or a DynamicDependency) and the Grouper<,> closure
    // (via TrimmerRootDescriptor). The PropertyNameGroupingRule is registered
    // explicitly by the user (opts.MessagePartitioning.GroupBy(...)), not
    // discovered reflectively, so the cascade stops here.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Property-name grouping is opt-in; consumers preserve the target property via DAM or trim descriptor. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed Grouper<,> resolved from runtime types; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed Grouper<,> resolved from runtime types; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
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
