namespace Wolverine.Configuration;

/// <summary>
///     Marks a code-generation <see cref="JasperFx.CodeGeneration.Frames.Frame" /> as deliberately
///     <b>not applicable</b> to F# code generation (issue GH-2969). Use this only for frames whose
///     semantics have no idiomatic F# emit — anything tied to constructs F# does not expose well
///     (e.g. <c>out</c> parameters, <c>ref</c> returns, expression-tree synthesis).
/// </summary>
/// <remarks>
///     <para>
///         The F# audit tracks every Wolverine <c>Frame</c> in one of three buckets, surfaced by the
///         <c>wolverine-diagnostics fsharp-coverage</c> command:
///     </para>
///     <list type="bullet">
///         <item><b>Implemented</b> — overrides <c>GenerateFSharpCode</c> to emit valid F#.</item>
///         <item><b>Intentionally skipped</b> — carries this attribute with <see cref="Skip" /> = true and a
///         recorded <see cref="Reason" />; never throws, never blocks generation (the code path that would
///         have hit it just isn't reachable from an F# host).</item>
///         <item><b>Remaining</b> — neither; still inherits the default-throwing
///         <c>Frame.GenerateFSharpCode</c> seam. These are the open audit items.</item>
///     </list>
///     <para>
///         An attribute is used rather than a virtual property because JasperFx's base <c>Frame</c> is a
///         nuget type Wolverine cannot extend, and Wolverine's frames inherit JasperFx's
///         <c>SyncFrame</c>/<c>AsyncFrame</c>/<c>MethodCall</c> directly (no shared Wolverine base to hang an
///         override on). The attribute is reflection-discoverable without instantiating the frame.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class FSharpEmitAttribute : Attribute
{
    /// <summary>
    ///     When true, the frame is intentionally excluded from F# code generation and counted in the
    ///     "skipped" bucket rather than "remaining".
    /// </summary>
    public bool Skip { get; set; }

    /// <summary>
    ///     A short, human-readable explanation of why the frame cannot emit F#. Required (in practice) when
    ///     <see cref="Skip" /> is true so the coverage report can show why each frame was excluded.
    /// </summary>
    public string? Reason { get; set; }
}
