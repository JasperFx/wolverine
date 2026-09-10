using JasperFx.Events.EventModeling;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Declare the Event Modeling <see cref="SlicePattern" /> of a message handler when nothing in the
///     code can derive it (GH-4395).
/// </summary>
/// <remarks>
///     <para>
///         A message handler's slice is a <see cref="SlicePattern.Command" /> or an
///         <see cref="SlicePattern.Automation" /> depending on <em>why</em> the message arrived -- a person
///         asked for it, or the system reacted to something -- and a handler signature cannot tell the two
///         apart, so Wolverine leaves the pattern unclaimed (GH-4387). The model can answer when another slice
///         in this application hands the message off, but a message that comes from another service over a
///         broker, from a hosted service, or from a controller that is not a Wolverine endpoint has no
///         producer in the model at all, and its slice carries no pattern.
///     </para>
///     <para>
///         This attribute puts the fact back next to the code, the same way <see cref="EmitsAttribute" />
///         does for event types. It is read on the <see cref="EventModelProvenance.Derived" /> rung and it
///         is purely diagnostic: nothing about dispatch, codegen or persistence reads it, so a wrong or stale
///         declaration costs a claim that disagrees with the board, which is exactly the drift the merge
///         exists to surface.
///     </para>
///     <para>
///         <b>It fills a gap and never overrides a trigger.</b> An HTTP route, a gRPC RPC, a schedule and an
///         inbound external system each derive the pattern from the trigger itself, and the attribute does
///         not contradict them. On a plain message handler it does take precedence over the model-wide
///         inference that promotes a slice to <see cref="SlicePattern.Automation" /> because another slice
///         cascades its message: that inference is exactly the guess the declaration is there to settle.
///     </para>
///     <para>
///         Put it on the handler method, or on the handler type when every method of it has the same
///         pattern. A declaration on the method wins over one on the type.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [SlicePattern(SlicePattern.Command)]
///     public static IssueAssigned Handle(AssignIssue command, IssueRepository issues)
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SlicePatternAttribute : Attribute
{
    public SlicePatternAttribute(SlicePattern pattern)
    {
        Pattern = pattern;
    }

    /// <summary>The slice pattern this handler declares.</summary>
    public SlicePattern Pattern { get; }
}
