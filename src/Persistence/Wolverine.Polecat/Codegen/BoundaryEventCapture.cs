using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Polecat.Events.Dcb;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Polecat.Codegen;

/// <summary>
/// Registers a collection of events via IEventBoundary.AppendMany() for DCB workflows.
/// </summary>
internal class RegisterBoundaryEventsFrame<T> : MethodCall where T : class
{
    public RegisterBoundaryEventsFrame(Variable returnVariable) : base(typeof(IEventBoundary<T>),
        FindMethod(returnVariable.VariableType))
    {
        Arguments[0] = returnVariable;
        CommentText = "Capturing events returned from handler and appending via DCB boundary";
    }

    internal static MethodInfo FindMethod(Type responseType)
    {
        return responseType.CanBeCastTo<IEnumerable<object>>()
            ? ReflectionHelper.GetMethod<IEventBoundary<T>>(x => x.AppendMany(new List<object>()))!
            : ReflectionHelper.GetMethod<IEventBoundary<T>>(x => x.AppendOne(null))!;
    }
}

/// <summary>
/// Handles async enumerable return values by appending each event via IEventBoundary.AppendOne().
/// </summary>
internal class ApplyBoundaryEventsFromAsyncEnumerableFrame<T> : AsyncFrame where T : class
{
    private readonly Variable _returnValue;
    private Variable? _boundary;

    public ApplyBoundaryEventsFromAsyncEnumerableFrame(Variable returnValue)
    {
        _returnValue = returnValue;
        uses.Add(returnValue);
    }

    public string Description => "Append events from async enumerable to DCB boundary for " +
                                 typeof(T).FullNameInCode();

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _boundary = chain.FindVariable(typeof(IEventBoundary<T>));
        yield return _boundary;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var variableName = (typeof(T).Name + "Event").ToCamelCase();

        writer.WriteComment(Description);
        writer.Write(
            $"await foreach (var {variableName} in {_returnValue.Usage}) {_boundary!.Usage}.{nameof(IEventBoundary<string>.AppendOne)}({variableName});");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Makes each individual return value from a handler method be appended as an event
/// via IEventBoundary.AppendOne() for DCB workflows.
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
