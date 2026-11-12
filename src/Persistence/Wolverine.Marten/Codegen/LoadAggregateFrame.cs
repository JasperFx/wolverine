using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using JasperFx.Events;
using Wolverine.Persistence.EventSourcing;
using CoreConcurrencyStyle = Wolverine.Persistence.EventSourcing.ModelConcurrencyStyle;

namespace Wolverine.Marten.Codegen;

/// <summary>
///     Marten's batchable variant of Wolverine core's aggregate load (GH-5141). The standalone path, in C# and F#,
///     is core's; this adds only the enlistment in a Marten batched query that <see cref="MartenBatchingPolicy" />
///     performs.
/// </summary>
internal class LoadAggregateFrame : Wolverine.Persistence.EventSourcing.Codegen.LoadAggregateFrame, IBatchableFrame
{
    private Variable? _batchQuery;
    private Variable? _batchQueryItem;

    public LoadAggregateFrame(AggregateLoadRequest att) : base(att, typeof(IDocumentSession), "Marten")
    {
    }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        // GH-4567: this used to emit batchQuery.Events.FetchForWriting<T, TNaturalKey>(...), which is
        // precisely the overload IBatchedQuery does not have -- so had it ever run it would have emitted
        // code that does not compile. It could not run: MartenBatchingPolicy is the only thing that enlists
        // these frames and MartenBatchingPolicy.IsBatchable refuses natural-key loads for exactly that
        // reason. Kept as a throw rather than deleted so that relaxing IsBatchable fails here, loudly and
        // at codegen time, instead of producing an uncompilable generated type.
        if (_att.IsNaturalKey)
        {
            throw new InvalidOperationException(
                $"A natural-key aggregate load for {_att.AggregateType.FullNameInCode()} cannot be batched: " +
                $"IBatchedQuery has no FetchForWriting<T, TNaturalKey> overload. " +
                $"{nameof(MartenBatchingPolicy)}.IsBatchable is supposed to have excluded this frame.");
        }

        if (_att.LoadStyle == CoreConcurrencyStyle.Exclusive)
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForExclusiveWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage});");
        }
        else if (_version == null)
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage});");
        }
        else
        {
            writer.WriteLine($"var {_batchQueryItem!.Usage} = {_batchQuery!.Usage}.Events.FetchForWriting<{_att.AggregateType.FullNameInCode()}>({_rawIdentity.Usage}, {_version.Usage});");
        }
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(_eventStreamType), Stream.Usage + "_BatchItem",
            this);
        _batchQuery = batchQuery;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var variable in base.FindVariables(chain))
        {
            yield return variable;
        }

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }
    }

    /// <summary>
    /// Set to true after GenerateCodeForBatchResolution has been called,
    /// to prevent duplicate variable declarations.
    /// </summary>
    internal bool BatchResolutionGenerated { get; private set; }

    /// <summary>
    /// When this frame is part of a batch query, generate only the code that
    /// resolves the batch item result (var stream_entity = await ...BatchItem).
    /// This is called by MartenBatchFrame after the batch execute, ensuring
    /// the variable is declared before any guard frames reference it.
    /// </summary>
    public void GenerateCodeForBatchResolution(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null) return;

        writer.WriteComment(Comment);
        writer.Write(
            $"var {Stream.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");

        writeConsistency(writer);

        BatchResolutionGenerated = true;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // If batch resolution already generated the variable declaration, skip
        if (BatchResolutionGenerated)
        {
            Next?.GenerateCode(method, writer);
            return;
        }

        if (_batchQueryItem == null)
        {
            base.GenerateCode(method, writer);
            return;
        }

        writer.WriteComment(Comment);
        writer.Write(
            $"var {Stream.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");

        writeConsistency(writer);

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (BatchResolutionGenerated)
        {
            Next?.GenerateFSharpCode(method, writer);
            return;
        }

        if (_batchQueryItem == null)
        {
            base.GenerateFSharpCode(method, writer);
            return;
        }

        writer.WriteComment(Comment);
        writer.WriteLine($"let! {Stream.Usage} = {_batchQueryItem.FSharpUsage}");

        writeFSharpConsistency(writer);

        Next?.GenerateFSharpCode(method, writer);
    }
}
