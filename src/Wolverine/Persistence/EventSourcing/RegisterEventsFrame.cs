using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// Appends a handler's returned event, or collection of events, onto the aggregate's event stream.
/// Shared by every event sourcing store integration — see GH-3907.
/// </summary>
/// <remarks>
/// <para>
/// GH-4752: this frame used to be <c>RegisterEventsFrame&lt;T&gt;</c>, closed over the aggregate type
/// through <c>CloseAndBuildAs</c> — that is, through <see cref="Activator" />. The chain model is built
/// at startup even under <c>TypeLoadMode.Static</c>, so a Native AOT application reached that
/// <c>Activator.CreateInstance</c> on the closed generic frame and died with
/// <c>MissingMethodException: No parameterless constructor defined</c>: the constructor was reachable
/// only reflectively, so ILC trimmed it. Taking the aggregate type as a plain constructor argument
/// makes the construction a <c>newobj</c> the compiler emits, which ILC cannot trim — a guarantee
/// rather than a promise, and it needs no <c>[DynamicDependency]</c> to hold.
/// </para>
/// <para>
/// The remaining reflection — closing <see cref="IEventStream{T}" /> over the aggregate type and
/// selecting the <c>AppendMany</c> overload off it — is the same shape
/// <see cref="EventCaptureActionSource" /> has always used, and the same
/// <c>typeof(IEventStream&lt;&gt;).MakeGenericType(...)</c> that
/// <see cref="AggregateHandling.FindEventStreamVariable" /> runs earlier in the very same
/// <c>Apply()</c> call. In GH-4752's native image that call had already succeeded by the time the
/// frame construction threw, so the interface close is demonstrably fine where the
/// <c>Activator</c> call was not.
/// </para>
/// </remarks>
internal class RegisterEventsFrame : MethodCall
{
    public RegisterEventsFrame(Variable returnVariable, Type aggregateType) : base(
        EventStreamTypeFor(aggregateType),
        FindMethod(aggregateType, returnVariable.VariableType))
    {
        Arguments[0] = returnVariable;
        CommentText = "Capturing any possible events returned from the command handlers";
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IEventStream<TAggregate> at codegen time, exactly as AggregateHandling.FindEventStreamVariable already does earlier in the same Apply() call.")]
    internal static Type EventStreamTypeFor(Type aggregateType) =>
        typeof(IEventStream<>).MakeGenericType(aggregateType);

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The reflected members are IEventStream<TAggregate>.AppendMany/AppendOne, named via nameof and preserved by the aggregate type's own registration. Same justification as EventCaptureActionSource.")]
    internal static MethodInfo FindMethod(Type aggregateType, Type responseType)
    {
        var streamType = EventStreamTypeFor(aggregateType);

        // AppendMany is overloaded - IEnumerable<object> and object[] - so the parameter types have to
        // be spelled out rather than looked up by name alone.
        return responseType.CanBeCastTo<IEnumerable<object>>()
            ? streamType.GetMethod(nameof(IEventStream<object>.AppendMany), [typeof(IEnumerable<object>)])!
            : streamType.GetMethod(nameof(IEventStream<object>.AppendOne), [typeof(object)])!;
    }
}
