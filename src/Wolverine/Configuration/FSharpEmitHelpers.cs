using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;

namespace Wolverine.Configuration;

/// <summary>
///     Small shared helper for emitting F# from Wolverine frames (issue GH-2969): the recurring
///     "abort-or-continue" continuation shape. (The former CastVariable workaround was retired once
///     JasperFx 2.2.5 added the built-in <c>Variable.FSharpUsage</c>; frames now use that directly.)
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
}
