using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Registers a collection of events via <see cref="IEventBoundary{T}.AppendMany(IEnumerable{object})" />
///     for DCB workflows.
/// </summary>
/// <remarks>
///     GH-3911: this and its siblings below were byte-identical in <c>Wolverine.Marten</c> and
///     <c>Wolverine.Polecat</c>. Nothing in them names a store — <see cref="IEventBoundary{T}" /> is
///     JasperFx.Events vocabulary — so they moved down whole.
///     <para>
///     GH-4752: the model type is a constructor argument rather than a generic parameter, for the same
///     reason as <see cref="RegisterEventsFrame" /> — closing this frame through <c>CloseAndBuildAs</c>
///     put an <see cref="Activator" /> call on a startup path that runs even under
///     <c>TypeLoadMode.Static</c>, and ILC trims the constructor of a generic frame nothing references
///     statically. This is the DCB twin of the crash reported on the single-stream path.
///     </para>
/// </remarks>
internal class RegisterBoundaryEventsFrame : MethodCall
{
    public RegisterBoundaryEventsFrame(Variable returnVariable, Type modelType) : base(
        BoundaryTypeFor(modelType),
        FindMethod(modelType, returnVariable.VariableType))
    {
        Arguments[0] = returnVariable;
        CommentText = "Capturing events returned from handler and appending via DCB boundary";
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IEventBoundary<TModel> at codegen time, exactly as DcbModelAttribute already does on the same path.")]
    internal static Type BoundaryTypeFor(Type modelType) => typeof(IEventBoundary<>).MakeGenericType(modelType);

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The reflected members are IEventBoundary<TModel>.AppendMany/AppendOne, named via nameof and preserved by the model type's own registration. Same justification as BoundaryEventCaptureActionSource.")]
    internal static MethodInfo FindMethod(Type modelType, Type responseType)
    {
        var boundaryType = BoundaryTypeFor(modelType);

        // AppendMany is overloaded - IEnumerable<object> and object[] - so the parameter types have to
        // be spelled out rather than looked up by name alone.
        return responseType.CanBeCastTo<IEnumerable<object>>()
            ? boundaryType.GetMethod(nameof(IEventBoundary<object>.AppendMany), [typeof(IEnumerable<object>)])!
            : boundaryType.GetMethod(nameof(IEventBoundary<object>.AppendOne), [typeof(object)])!;
    }
}

/// <summary>
///     Handles async enumerable return values by appending each event via
///     <see cref="IEventBoundary{T}.AppendOne" />.
/// </summary>
/// <remarks>
///     GH-4752: the model type is a constructor argument rather than a generic parameter — see
///     <see cref="RegisterBoundaryEventsFrame" />.
/// </remarks>
internal class ApplyBoundaryEventsFromAsyncEnumerableFrame : AsyncFrame
{
    private readonly Type _modelType;
    private readonly Variable _returnValue;
    private Variable? _boundary;

    public ApplyBoundaryEventsFromAsyncEnumerableFrame(Variable returnValue, Type modelType)
    {
        _returnValue = returnValue;
        _modelType = modelType;
        uses.Add(returnValue);
    }

    public string Description => "Append events from async enumerable to DCB boundary for " +
                                 _modelType.FullNameInCode();

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _boundary = chain.FindVariable(RegisterBoundaryEventsFrame.BoundaryTypeFor(_modelType));
        yield return _boundary;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var variableName = (_modelType.Name + "Event").ToCamelCase();

        writer.WriteComment(Description);
        writer.Write(
            $"await foreach (var {variableName} in {_returnValue.Usage}) {_boundary!.Usage}.{nameof(IEventBoundary<string>.AppendOne)}({variableName});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     Makes each individual return value from a handler method be appended as an event
///     via <see cref="IEventBoundary{T}.AppendOne" /> for DCB workflows.
/// </summary>
internal class BoundaryEventCaptureActionSource : IReturnVariableActionSource
{
    private readonly Type _aggregateType;

    public BoundaryEventCaptureActionSource(Type aggregateType)
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

        public string Description =>
            "Append event via DCB boundary for aggregate " + _aggregateType.FullNameInCode();

        public IEnumerable<Type> Dependencies()
        {
            yield break;
        }

        // Core's trim analysis is stricter than either integration ran; the behavior is identical to
        // the two copies this replaces. The reflective close happens at codegen time over the model
        // type, which AOT consumers pre-generate via TypeLoadMode.Static.
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "MethodCall reflects over IEventBoundary<TModel>.AppendOne, named via nameof and preserved by the closed generic that codegen instantiates. AOT consumers pre-generate via TypeLoadMode.Static.")]
        [UnconditionalSuppressMessage("AOT", "IL3050",
            Justification = "MakeGenericType closes IEventBoundary<TModel> at codegen time; AOT consumers pre-generate via TypeLoadMode.Static.")]
        public IEnumerable<Frame> Frames()
        {
            var boundaryType = typeof(IEventBoundary<>).MakeGenericType(_aggregateType);

            yield return new MethodCall(boundaryType, nameof(IEventBoundary<string>.AppendOne))
            {
                Arguments =
                {
                    [0] = _variable
                }
            };
        }
    }
}
