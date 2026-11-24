namespace Wolverine.Attributes;

/// <summary>
///     Pulls a sticky handler (see <see cref="StickyHandlerAttribute" />) onto the slots of a partitioned topology for
///     the message types that topology publishes. By default a sticky handler keeps running on its own endpoint, so it
///     is not sequenced with the message type's other handler types by group id. With this attribute it runs on the
///     topology's slot instead, one handler type after the other, in the order of the group's messages. It has no
///     effect on a handler that isn't sticky, which runs on the slot anyway, or on a message type that no partitioned
///     topology publishes
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RunOnPartitionSlotAttribute : Attribute;
