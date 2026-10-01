using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// The event-capture half of the aggregate handler workflow, shared by every event sourcing store
/// integration rather than copied into each one. See GH-3907.
///
/// <para>
/// Everything here is expressed purely in terms of <see cref="IEventStream{T}"/> from JasperFx.Events,
/// so it carries no dependency on Marten, Polecat or any other store — which is exactly why it is the
/// right first tenant of this namespace. Types stay <c>internal</c> and reach the store integrations
/// through <c>InternalsVisibleTo</c>, so nothing here is public API and the shape stays free to change
/// while the rest of the workflow is pulled down.
/// </para>
/// </summary>
internal static class EventSourcingDescriptions
{
    public const string AppendToStream = "Append event to event stream for aggregate ";
}

/// <summary>
/// Appends every event yielded by a handler's <c>IAsyncEnumerable&lt;object&gt;</c> return value onto
/// the aggregate's event stream.
/// </summary>
/// <remarks>
/// GH-4752: the aggregate type is a constructor argument rather than a generic parameter. This frame
/// used to be <c>ApplyEventsFromAsyncEnumerableFrame&lt;T&gt;</c> closed through <c>CloseAndBuildAs</c>,
/// i.e. through <see cref="Activator" /> — and because the chain model is built at startup even under
/// <c>TypeLoadMode.Static</c>, a Native AOT application reached that call and died with
/// <c>MissingMethodException: No parameterless constructor defined</c>, ILC having trimmed a constructor
/// nothing referenced statically. A plain constructor argument makes the construction a <c>newobj</c>
/// the compiler emits, which ILC cannot trim.
/// </remarks>
internal class ApplyEventsFromAsyncEnumerableFrame : AsyncFrame, IReturnVariableAction
{
    private readonly Type _aggregateType;
    private readonly Variable _returnValue;
    private readonly string _storeName;
    private Variable? _stream;

    public ApplyEventsFromAsyncEnumerableFrame(Variable returnValue, string storeName, Type aggregateType)
    {
        _returnValue = returnValue;
        _storeName = storeName;
        _aggregateType = aggregateType;
        uses.Add(_returnValue);
    }

    // Carries the store's own name because this string is written into the generated source as a
    // comment. Keeping it per-store means moving this type into core changes no generated output.
    public string Description => $"Apply events to {_storeName} event stream";

    public new IEnumerable<Type> Dependencies()
    {
        yield break;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return this;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _stream = chain.FindVariable(RegisterEventsFrame.EventStreamTypeFor(_aggregateType));
        yield return _stream;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var variableName = (_aggregateType.Name + "Event").ToCamelCase();

        writer.WriteComment(Description);
        writer.Write(
            $"await foreach (var {variableName} in {_returnValue.Usage}) {_stream!.Usage}.{nameof(IEventStream<string>.AppendOne)}({variableName});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Treats each of a handler's return values as an event to append to the aggregate's stream — the
/// default behavior when a handler neither returns <c>Events</c>/<c>IEnumerable&lt;object&gt;</c> nor
/// takes an <see cref="IEventStream{T}"/> parameter.
/// </summary>
internal class EventCaptureActionSource : IReturnVariableActionSource
{
    private readonly Type _aggregateType;

    public EventCaptureActionSource(Type aggregateType)
    {
        _aggregateType = aggregateType;
    }

    public IReturnVariableAction Build(IChain chain, Variable variable)
    {
        return new ActionSource(_aggregateType, variable);
    }

    internal class ActionSource : IReturnVariableAction
    {
        private readonly Type _aggregateType;
        private readonly Variable _variable;

        public ActionSource(Type aggregateType, Variable variable)
        {
            _aggregateType = aggregateType;
            _variable = variable;
        }

        public string Description => EventSourcingDescriptions.AppendToStream + _aggregateType.FullNameInCode();

        public IEnumerable<Type> Dependencies()
        {
            yield break;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2062",
            Justification = "streamType = MakeGenericType(IEventStream<>, _aggregateType) at codegen time; AppendOne is statically referenced via nameof and the closed-generic IEventStream<TAggregate>.AppendOne method is preserved by the aggregate-type registration. AOT consumers pre-generate via TypeLoadMode.Static.")]
        // IL2026 is surfaced only now that this lives in Wolverine core, which runs trim analysis that
        // Wolverine.Marten/Wolverine.Polecat did not - the behavior is unchanged from both copies. The
        // reflected member is IEventStream<TAggregate>.AppendOne, named via nameof and preserved by the
        // same aggregate-type registration the IL2062 justification above relies on.
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "MethodCall reflects over IEventStream<TAggregate>.AppendOne, statically referenced via nameof and preserved by the aggregate-type registration. AOT consumers pre-generate via TypeLoadMode.Static.")]
        [UnconditionalSuppressMessage("AOT", "IL3050",
            Justification = "MakeGenericType closes IEventStream<TAggregate> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static so the reflective close never fires.")]
        public IEnumerable<Frame> Frames()
        {
            var streamType = typeof(IEventStream<>).MakeGenericType(_aggregateType);

            var append = new MethodCall(streamType, nameof(IEventStream<string>.AppendOne))
            {
                Arguments =
                {
                    [0] = _variable
                }
            };

            // GH-4309: a handler that sometimes has nothing to append says so by returning null —
            // the same no-op shape a null cascaded message has always had. Guard the append the
            // way the Events/EventsToAppend path already does with WrapIfNotNull, rather than
            // letting the null reach IEventStream.AppendOne, which throws ArgumentNullException
            // from inside the store.
            yield return couldBeNull(_variable.VariableType) ? append.WrapIfNotNull(_variable) : append;
        }

        // A non-nullable struct event can never be null, and `!= null` against one is not even
        // legal C# — emit the guard only where a null is representable.
        private static bool couldBeNull(Type type)
            => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }
}
