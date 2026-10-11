using System.ComponentModel;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Tags;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     GH-4940. Called by Wolverine's generated code for every Dynamic Consistency Boundary load, so a tracked
///     session can say which tag query a handler's decision was consistent over. Not for application code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class DcbBoundaryTracking
{
    /// <summary>
    ///     Record that the work <paramref name="context" /> is handling loaded <paramref name="modelType" /> over
    ///     <paramref name="query" />. Does nothing unless a tracked session is active, which is only ever in a test.
    /// </summary>
    public static void Record(MessageContext context, Type modelType, EventTagQuery query)
    {
        if (context.Runtime is WolverineRuntime { ActiveSession: not null } runtime)
        {
            runtime.RecordLoadedBoundary(context, modelType, query);
        }
    }
}

/// <summary>
///     GH-4940. Added by <see cref="DcbModelAttribute" /> beside the store's own boundary load frame, so the
///     recording is one frame in core rather than one per store: it reads the same <see cref="EventTagQuery" />
///     variable the store's <c>FetchForWritingByTags</c> call does.
/// </summary>
internal class RecordLoadedBoundaryFrame : SyncFrame
{
    private readonly Type _modelType;
    private Variable? _context;
    private Variable? _query;

    public RecordLoadedBoundaryFrame(Type modelType)
    {
        _modelType = modelType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _query = chain.FindVariable(typeof(EventTagQuery));
        yield return _query;

        // Every message handler has one, and an HTTP endpoint that opens an outbox session has one too. A
        // chain without one has no tracked session to tell, so it records nothing.
        _context = chain.TryFindVariable(typeof(MessageContext), VariableSource.All);
        if (_context != null) yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.WriteLine(
                $"{typeof(DcbBoundaryTracking).FullNameInCode()}.{nameof(DcbBoundaryTracking.Record)}({_context.Usage}, typeof({_modelType.FullNameInCode()}), {_query!.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.WriteLine(
                $"{typeof(DcbBoundaryTracking).FSharpName()}.{nameof(DcbBoundaryTracking.Record)}({_context.FSharpUsage}, typeof<{_modelType.FSharpName()}>, {_query!.FSharpUsage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
