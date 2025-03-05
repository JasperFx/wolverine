using System.Linq.Expressions;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Handlers;

public static class CodeGenExtensions
{
    internal static readonly string ReturnActionKey = "ReturnAction";

    public static bool HasReturnAction(this Variable variable)
    {
        return variable.Properties.ContainsKey(ReturnActionKey);
    }

    /// <summary>
    /// Does this frame possibly send any kind of cascading messages?
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public static bool MaySendMessages(this Frame frame)
    {
        if (frame is CaptureCascadingMessages) return true;
        if (frame is MethodCall call)
        {
            if (call.Method.GetParameters().Any(x =>
                    x.ParameterType == typeof(IMessageBus) || x.ParameterType == typeof(MessageContext) ||
                    x.ParameterType == typeof(IMessageContext)))
            {
                return true;
            }

            if (call.CreatesNewOf<OutgoingMessages>()) return true;
        }

        return false;
    }
    
    /// <summary>
    ///     Override how Wolverine will generate code to handle this value returned from a handler call
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="action"></param>
    public static void UseReturnAction(this Variable variable, IReturnVariableAction action)
    {
        variable.Properties[ReturnActionKey] = action;
    }

    /// <summary>
    ///     Override how Wolverine will generate code to handle this value returned from a handler call
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="frameSource"></param>
    /// <param name="description"></param>
    /// <returns></returns>
    public static ReturnVariableAction UseReturnAction(this Variable variable, Func<Variable, Frame> frameSource,
        string? description = null)
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

    /// <summary>
    ///     Fetch the code generation handling strategy for this variable
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="chain"></param>
    /// <returns></returns>
    public static IReturnVariableAction ReturnAction(this Variable variable, IChain chain)
    {
        if (chain == null)
        {
            throw new ArgumentNullException(nameof(chain));
        }

        if (variable.Properties.TryGetValue(ReturnActionKey, out var raw))
        {
            if (raw is IReturnVariableAction action)
            {
                return action;
            }
        }

        return chain.ReturnVariableActionSource.Build(chain, variable);
    }

    /// <summary>
    ///     Override how Wolverine generates code to handle this return value by calling a method on the
    ///     value returned
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="expression"></param>
    /// <param name="description"></param>
    /// <typeparam name="T"></typeparam>
    public static void CallMethodOnReturnVariable<T>(this Variable variable, Expression<Action<T>> expression,
        string? description = null)
    {
        var action = new CallMethodReturnVariableAction<T>(variable, expression);
        action.Description = description ?? action.MethodCall.ToString();
        action.MethodCall.CommentText = description;

        variable.UseReturnAction(action);
    }

    /// <summary>
    ///     Override how Wolverine generates code to handle this return value by calling a method on the
    ///     value returned
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="expression"></param>
    /// <param name="description"></param>
    /// <typeparam name="T"></typeparam>
    public static void CallMethodOnReturnVariableIfNotNull<T>(this Variable variable, Expression<Action<T>> expression,
        string? description = null)
    {
        var action = new CallMethodReturnVariableAction<T>(variable, expression);
        action.Description = description ?? action.MethodCall.ToString();
        action.MethodCall.CommentText = description;
        action.IfNotNullChecking = true;

        variable.UseReturnAction(action);
    }

    /// <summary>
    ///     Mark this return variable as being ignored as a cascaded message.
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="description">Optional description of why this variable is not cascaded</param>
    public static void DoNothingWithReturnValue(this Variable variable, string? description = null)
    {
        var action = new ReturnVariableAction { Description = "Do nothing" };
        action.Frames.Add(new CommentFrame(description ?? $"Variable {variable.Usage} was explicitly ignored"));
        variable.UseReturnAction(action);
    }

    /// <summary>
    ///     Wrap the current frame in an if (variable != null) block
    /// </summary>
    /// <param name="frame"></param>
    /// <param name="variable"></param>
    /// <returns></returns>
    public static Frame WrapIfNotNull(this Frame frame, Variable variable)
    {
        return new IfElseNullGuardFrame.IfNullGuardFrame(variable, frame);
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
    public List<Type> Dependencies { get; } = new();
    public List<Frame> Frames { get; } = new();
    public string Description { get; set; } = "Override";


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
#pragma warning disable CS8618
    public CallMethodReturnVariableAction(Variable variable, Expression<Action<T>> expression)
#pragma warning restore CS8618
    {
        MethodCall = MethodCall.For(expression);
        MethodCall.Target = variable;
    }

    public MethodCall MethodCall { get; }
    public bool IfNotNullChecking { get; set; }

    public string Description { get; set; }

    public IEnumerable<Type> Dependencies()
    {
        foreach (var parameter in MethodCall.Method.GetParameters()) yield return parameter.ParameterType;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return IfNotNullChecking ? MethodCall.WrapIfNotNull(MethodCall.Target!) : MethodCall;
    }
}

public interface IReturnVariableAction
{
    string Description { get; }
    IEnumerable<Type> Dependencies();
    IEnumerable<Frame> Frames();
}

public class CommentReturnAction : IReturnVariableAction
{
    private readonly string _commentText;

    public CommentReturnAction(string commentText)
    {
        _commentText = commentText;
    }

    public string Description { get; } = "Comment Only";
    public IEnumerable<Type> Dependencies()
    {
        yield break;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return new CommentFrame(_commentText);
    }
}