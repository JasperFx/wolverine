using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb.Internals;

internal class LoadDocumentFrame : AsyncFrame
{
    private readonly bool _partitionById;
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _container;

    public LoadDocumentFrame(Type sagaType, Variable sagaId, bool partitionById = false)
    {
        _sagaId = sagaId;
        _partitionById = partitionById;
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    /// <summary>
    ///     The partition the document lives in: its own, keyed by the saga id, when the application opted into
    ///     <see cref="CosmosDbConfiguration.PartitionSagasById" /> — otherwise CosmosDB's single "undefined"
    ///     partition, which is where a document with no partition key value lands (GH-3415).
    /// </summary>
    public static string PartitionKeyExpression(Variable id, bool partitionById)
    {
        return partitionById
            ? $"new {typeof(PartitionKey).FullNameInCode()}({id.Usage})"
            : $"{typeof(PartitionKey).FullNameInCode()}.None";
    }

    public static string FSharpPartitionKeyExpression(Variable id, bool partitionById)
    {
        return partitionById
            ? $"{typeof(PartitionKey).FSharpName()}({id.FSharpUsage})"
            : $"{typeof(PartitionKey).FSharpName()}.None";
    }

    /// <summary>
    ///     Name of the local variable that carries the CosmosDB ETag captured when the saga document
    ///     was read. <see cref="CosmosDbUpsertFrame" /> and <see cref="CosmosDbDeleteDocumentFrame" />
    ///     feed it back as an <see cref="ItemRequestOptions.IfMatchEtag" /> so the write becomes a
    ///     compare-and-swap. Derived from the document variable so that two loads in one method
    ///     (say, two <c>[Entity]</c> parameters) cannot collide.
    /// </summary>
    public static string EtagVariableName(Variable document)
    {
        return $"{document.Usage}_Etag";
    }

    /// <summary>
    ///     Optimistic concurrency applies to saga documents, and only to documents Wolverine read into
    ///     a local through this frame — that read is what declares the ETag local the write depends on.
    ///     Entity storage actions (<c>Storage.Update()</c>, <c>Storage.Store()</c>) hand the provider a
    ///     synthetic member access like <c>update1.Entity</c> with no preceding read, so they stay
    ///     last-write-wins exactly as they were.
    /// </summary>
    public static bool UsesOptimisticConcurrency(Variable document)
    {
        return document.VariableType.CanBeCastTo<Saga>() && !document.Usage.Contains('.');
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var capturesEtag = UsesOptimisticConcurrency(Saga);
        var etag = EtagVariableName(Saga);

        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document from CosmosDB");
        writer.Write(
            $"{Saga.VariableType.FullNameInCode()} {Saga.Usage} = default;");

        if (capturesEtag)
        {
            writer.WriteComment(
                "Capture the ETag so the eventual write can be a compare-and-swap against this exact revision");
            writer.Write($"string {etag} = null;");
        }

        writer.Write($"try");
        writer.Write($"{{");
        writer.Write(
            $"    var _cosmosResponse = await {_container!.Usage}.ReadItemAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {PartitionKeyExpression(_sagaId, _partitionById)}, cancellationToken: {_cancellation!.Usage}).ConfigureAwait(false);");
        writer.Write($"    {Saga.Usage} = _cosmosResponse.Resource;");

        if (capturesEtag)
        {
            writer.Write($"    {etag} = _cosmosResponse.ETag;");
        }

        writer.Write($"}}");
        writer.Write(
            $"catch ({typeof(CosmosException).FullNameInCode()} e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)");
        writer.Write($"{{");
        writer.Write($"    {Saga.Usage} = default;");
        writer.Write($"}}");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var capturesEtag = UsesOptimisticConcurrency(Saga);
        var etag = EtagVariableName(Saga);

        writer.BlankLine();
        writer.WriteComment("Try to load the existing saga document from CosmosDB");
        // Declare as mutable so it can be assigned in either the try or the with branch.
        writer.Write($"let mutable {Saga.FSharpUsage} = Unchecked.defaultof<{Saga.VariableType.FSharpName()}>");

        if (capturesEtag)
        {
            writer.WriteComment(
                "Capture the ETag so the eventual write can be a compare-and-swap against this exact revision");
            writer.Write($"let mutable {etag} : string = null");
        }

        writer.Write("BLOCK:try");
        writer.Write(
            $"let! _cosmosResponse = {_container!.FSharpUsage}.ReadItemAsync<{Saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {FSharpPartitionKeyExpression(_sagaId, _partitionById)}, cancellationToken = {_cancellation!.FSharpUsage})");
        writer.Write($"{Saga.FSharpUsage} <- _cosmosResponse.Resource");

        if (capturesEtag)
        {
            writer.Write($"{etag} <- _cosmosResponse.ETag");
        }

        writer.FinishBlock();
        // Single-case with; guard ensures non-404 CosmosExceptions propagate.
        writer.Write(
            $"BLOCK:with :? {typeof(CosmosException).FSharpName()} as e when e.StatusCode = System.Net.HttpStatusCode.NotFound ->");
        writer.Write("()");
        writer.FinishBlock();

        Next?.GenerateFSharpCode(method, writer);
    }
}
