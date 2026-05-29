using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Configuration;

/// <summary>
///     Small shared helpers for emitting F# from Wolverine frames (issue GH-2969). These cover gaps in
///     the JasperFx-layer model where a value's C# rendering is not valid F#, plus the recurring
///     "abort-or-continue" continuation shape.
/// </summary>
internal static class FSharpEmitHelpers
{
    /// <summary>
    ///     Emits the F# form of a C# "abort the handler if <paramref name="conditionExpression" /> is
    ///     true, otherwise run the rest of the chain" guard. F# has no early <c>return</c>, so the
    ///     remainder of the chain (<paramref name="next" />) is rendered inside the <c>else</c> branch.
    ///     The abort branch is a no-op <c>()</c>: the method's <c>Task</c> result comes from the enclosing
    ///     <c>task { }</c> body or the machinery-appended trailing <c>Task.CompletedTask</c>, so both
    ///     branches are <c>unit</c> and the guard sits cleanly in statement position. Mirrors the C#
    ///     <c>if (cond) return; &lt;next&gt;</c>.
    /// </summary>
    public static void WriteAbortGuard(ISourceWriter writer, GeneratedMethod method, string conditionExpression,
        Frame? next)
    {
        writer.Write($"BLOCK:if {conditionExpression} then");
        writer.Write("()");
        writer.FinishBlock();

        writer.Write("BLOCK:else");
        if (next != null)
        {
            next.GenerateFSharpCode(method, writer);
        }
        else
        {
            writer.Write("()");
        }

        writer.FinishBlock();
    }

    /// <summary>
    ///     The F# rendering of a variable's usage. F# has no C-style cast, but
    ///     <see cref="CastVariable" /> bakes a C# <c>((Type)x)</c> cast into its <see cref="Variable.Usage" />
    ///     (e.g. the injected <c>ILogger&lt;TMessage&gt;</c> handed to validation frames as <c>ILogger</c>).
    ///     Rewrite that as an F# upcast <c>(x :&gt; Type)</c>; everything else uses its usage verbatim.
    /// </summary>
    /// <remarks>
    ///     The proper fix is an F#-aware usage on JasperFx's <c>CastVariable</c> itself; tracked as an
    ///     upstream JasperFx gap. Until then this keeps the audit moving without leaving Wolverine.
    /// </remarks>
    public static string FSharpUsage(Variable variable)
    {
        return variable is CastVariable cast
            ? $"({cast.Inner.Usage} :> {cast.VariableType.FSharpName()})"
            : variable.Usage;
    }
}
