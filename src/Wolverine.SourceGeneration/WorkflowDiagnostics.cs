using Microsoft.CodeAnalysis;

namespace Wolverine.SourceGeneration;

/// <summary>
/// GH-5660 SPIKE. What the first cut of the durable workflow generator refuses. Every one of these is an error except
/// <see cref="UncorrelatedWait" />, and a workflow with an error gets no generated saga.
/// </summary>
internal static class WorkflowDiagnostics
{
    private const string Category = "Wolverine.Workflows";

    public static readonly DiagnosticDescriptor NotStatic = error("WOLV1001",
        "A durable workflow must be a static method",
        "[DurableWorkflow] method {0} must be static: an instance would hold state outside the generated saga");

    public static readonly DiagnosticDescriptor AwaitOnSomethingElse = error("WOLV1002",
        "A durable workflow can only await IWorkflowContext members",
        "A [DurableWorkflow] method can only await members of IWorkflowContext; '{0}' is not one (do I/O through ctx.RunAsync(...))");

    public static readonly DiagnosticDescriptor LoopAcrossAwait = error("WOLV1003",
        "A loop can't contain a workflow wait",
        "'{0}' waits for a message inside a {1} loop; the first cut can't split a loop into saga steps");

    public static readonly DiagnosticDescriptor BlockAcrossAwait = error("WOLV1004",
        "try/catch/finally/using can't span a workflow wait",
        "'{0}' waits for a message inside a {1}; the block would have to stay open between two messages, which a saga step can't do");

    public static readonly DiagnosticDescriptor WaitNotAStatement = error("WOLV1005",
        "A workflow wait must be a statement of its own",
        "'{0}' must be awaited directly as a statement of the workflow method: 'var x = await ctx...;', 'x = await ctx...;' or 'await ctx...;'");

    public static readonly DiagnosticDescriptor UnsupportedConstruct = error("WOLV1006",
        "Construct not supported around a workflow wait",
        "The first cut can't split a workflow around a {0}");

    public static readonly DiagnosticDescriptor LocalNotHoistable = error("WOLV1007",
        "A local that lives across a wait must be a plain local",
        "'{0}' is used after a wait, so it is stored on the saga; declare it as a single plain local ('var {0} = ...;'), not as a pattern, out, const, using or multi-variable declaration");

    public static readonly DiagnosticDescriptor BadShape = error("WOLV1008",
        "Unsupported durable workflow method shape",
        "[DurableWorkflow] method {0}: {1}");

    public static readonly DiagnosticDescriptor NotSerializable = error("WOLV1009",
        "A local that lives across a wait must be serializable",
        "'{0}' is used after a wait, so it is stored on the saga, but its type {1} can't be stored ({2})");

    public static readonly DiagnosticDescriptor PrivateMember = error("WOLV1010",
        "A workflow can't use private members of its class",
        "'{0}' is private to {1}; the generated saga is a separate class, so make it internal or public");

    public static readonly DiagnosticDescriptor UncorrelatedWait = new("WOLV1011",
        "WaitForAsync<T> on a message that doesn't name its workflow",
        "{0} has no [SagaIdentity], Id or SagaId member, so it can only reach this workflow through the saga-id header (a reply to something the workflow sent)",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static DiagnosticDescriptor? Find(string id)
    {
        foreach (var descriptor in All)
        {
            if (descriptor.Id == id) return descriptor;
        }

        return null;
    }

    public static readonly DiagnosticDescriptor[] All =
    [
        NotStatic, AwaitOnSomethingElse, LoopAcrossAwait, BlockAcrossAwait, WaitNotAStatement, UnsupportedConstruct,
        LocalNotHoistable, BadShape, NotSerializable, PrivateMember, UncorrelatedWait
    ];

    private static DiagnosticDescriptor error(string id, string title, string message)
    {
        return new DiagnosticDescriptor(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
    }
}
