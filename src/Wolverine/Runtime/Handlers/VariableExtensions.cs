using System.Linq.Expressions;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Wolverine.Middleware;

namespace Wolverine.Runtime.Handlers;

public static class VariableExtensions
{
    internal static readonly string ReturnActionKey = "ReturnAction";
    
    public static ReturnVariableAction UseReturnAction(this Variable variable, Func<Variable, Frame> frameSource, string? description = null)
    {
        var frame = frameSource(variable);
        var action = new ReturnVariableAction();
        action.Frames.Add(frame);
        variable.Properties[ReturnActionKey] = action;

        if (description.IsNotEmpty())
        {
            action.Description = description;
        }

        return action;
    }

    public static IReturnVariableAction ReturnAction(this Variable variable)
    {
        if (variable.Properties.TryGetValue(ReturnActionKey, out var raw))
        {
            if (raw is IReturnVariableAction action) return action;
        }

        return new CascadeMessage(variable);
    }
    
    public static void CallMethodOnReturnVariable<T>(this Variable variable, Expression<Action<T>> expression, string? description = null)
    {
        var action = new CallMethodReturnVariableAction<T>(variable, expression);
        action.Description = description ?? action.MethodCall.ToString();
        action.MethodCall.CommentText = description;

        variable.Properties[ReturnActionKey] = action;

    }

    /// <summary>
    /// Mark this return variable as being ignored as a cascaded message.
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="description">Optional description of why this variable is not cascaded</param>
    public static void DoNothingWithReturnValue(this Variable variable, string? description = null)
    {
        var action = new ReturnVariableAction { Description = "Do nothing" };
        action.Frames.Add(new CommentFrame(description ?? $"Variable {variable.Usage} was explicitly ignored"));
        variable.Properties[ReturnActionKey] = action;
    }

    /// <summary>
    /// Wrap the current frame in an if (variable != null) block
    /// </summary>
    /// <param name="frame"></param>
    /// <param name="variable"></param>
    /// <returns></returns>
    public static IfNotNullFrame WrapIfNotNull(this Frame frame, Variable variable)
    {
        return new IfNotNullFrame(variable, frame);
    }
    
    
}

internal class CascadeMessage : IReturnVariableAction
{
    public CascadeMessage(Variable variable)
    {
        Variable = variable;
    }

    public Variable Variable { get; }

    public string Description => "Publish Cascading Message";
    public IEnumerable<Type> Dependencies()
    {
        yield break;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return new CaptureCascadingMessages(Variable);
    }
}

public class ReturnVariableAction : IReturnVariableAction
{
    public string Description { get; set; } = "Override";
    public List<Type> Dependencies { get; } = new();
    public List<Frame> Frames { get; } = new();
    

    IEnumerable<Type> IReturnVariableAction.Dependencies()
    {
        return Dependencies;
    }

    IEnumerable<Frame> IReturnVariableAction.Frames()
    {
        return Frames;
    }
}

public class CallMethodReturnVariableAction<T> : IReturnVariableAction
{
    public CallMethodReturnVariableAction(Variable variable, Expression<Action<T>> expression)
    {
        MethodCall = MethodCall.For(expression);
        MethodCall.Target = variable;
    }

    public string Description { get; set; }
    public MethodCall MethodCall { get; }
    public IEnumerable<Type> Dependencies()
    {
        foreach (var parameter in MethodCall.Method.GetParameters())
        {
            yield return parameter.ParameterType;
        }
    }

    public IEnumerable<Frame> Frames()
    {
        yield return MethodCall;
    }
}

public interface IReturnVariableAction
{
    string Description { get; }
    IEnumerable<Type> Dependencies();
    IEnumerable<Frame> Frames();
}