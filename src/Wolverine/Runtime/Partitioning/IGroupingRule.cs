namespace Wolverine.Runtime.Partitioning;

#region sample_IGroupingRule

/// <summary>
/// Strategy for determining the GroupId of a message
/// </summary>
public interface IGroupingRule
{
    bool TryFindIdentity(Envelope envelope, out string groupId);
}

#endregion