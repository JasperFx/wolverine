using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Logging;

/// <summary>
/// GH-5310. A derived variable of a generated message handler or HTTP endpoint that resolves the message's own
/// span (or the request span for an HTTP endpoint). Every frame that tags the span (<see cref="TagHandlerFrame"/>,
/// <see cref="AuditToActivityFrame"/>, the saga and aggregate frames) writes through it instead of reading
/// <see cref="Activity.Current"/>, which a persistence integration may have moved: Marten's
/// <c>marten.connection</c> activity becomes current as soon as the session is built when
/// <c>TrackConnections</c> is on, and stays current for the rest of the handler.
/// </summary>
/// <remarks>
/// This is an expression rather than a local captured by a frame at the top of the method, because the
/// frame arranger puts the creators of variables that no explicit frame owns (the Marten session among them)
/// ahead of every explicit frame, so no frame can be guaranteed to run before the session is built.
/// </remarks>
internal class MessageActivityVariable : Variable
{
    private readonly Func<string, string> _expression;

    /// <param name="root">The method argument the expression reads from (the handler's MessageContext, the
    /// endpoint's HttpContext). Frames using this variable also use the root, so F# does not discard it as unused.</param>
    /// <param name="expression">Builds the expression from the root argument's usage.</param>
    public MessageActivityVariable(Variable root, Func<string, string> expression)
        : base(typeof(Activity), expression(root.Usage))
    {
        Root = root;
        _expression = expression;
    }

    public Variable Root { get; }

    public override string Usage => _expression(Root.Usage);

    // F# takes `isNull f(x)` and `f(x).SetTag(...)` as successive arguments, so the method call is parenthesized
    public override string FSharpUsage => $"({_expression(Root.FSharpUsage)})";

    /// <summary>
    /// The message-span variable of the generated method, or null when the chain registers none.
    /// Call this from a frame's <c>FindVariables</c> and yield what <see cref="Uses"/> returns for it.
    /// </summary>
    public static MessageActivityVariable? TryFind(IMethodVariables chain)
    {
        return chain.TryFindVariable(typeof(Activity), VariableSource.Existing) as MessageActivityVariable;
    }

    /// <summary>
    /// The variables a frame tagging through <paramref name="activity"/> depends on.
    /// </summary>
    public static IEnumerable<Variable> Uses(MessageActivityVariable? activity)
    {
        if (activity == null) yield break;

        yield return activity;
        yield return activity.Root;
    }

    /// <summary>
    /// C# expression for the message span: the variable, or <c>Activity.Current</c> as the fallback.
    /// </summary>
    public static string UsageFor(Variable? activity)
    {
        return activity?.Usage ??
               $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}";
    }

    /// <summary>
    /// F# expression for the message span: the variable, or <c>Activity.Current</c> as the fallback.
    /// </summary>
    public static string FSharpUsageFor(Variable? activity)
    {
        return activity?.FSharpUsage ??
               $"{typeof(Activity).FSharpName()}.{nameof(Activity.Current)}";
    }
}
