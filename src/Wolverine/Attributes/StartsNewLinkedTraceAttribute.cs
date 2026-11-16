namespace Wolverine.Attributes;

/// <summary>
/// GH-5537. Every outgoing message of the decorated type starts a <b>new trace</b> on the receiving
/// side, with a span link back to the activity that sent it, on every route (local, external and
/// cascaded). Use it for fan-out messages that would otherwise make one enormous trace. An explicit
/// <see cref="DeliveryOptions.StartsNewLinkedTrace" /> on a send wins over the attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class StartsNewLinkedTraceAttribute : ModifyEnvelopeAttribute
{
    public override void Modify(Envelope envelope)
    {
        envelope.StartsNewLinkedTrace = true;
    }
}
