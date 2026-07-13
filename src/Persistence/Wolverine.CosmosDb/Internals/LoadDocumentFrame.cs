using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb.Internals;

internal class LoadDocumentFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _container;

    public LoadDocumentFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _container = chain.FindVariable(typeof(Container));
        yield return _container;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document from CosmosDB");
        writer.Write(
            $"{Saga.VariableType.FullNameInCode()} {Saga.Usage} = default;");
        writer.Write($"try");
        writer.Write($"{{");
        writer.Write(
            $"    var _cosmosResponse = await {_container!.Usage}.ReadItemAsync<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}, {typeof(PartitionKey).FullNameInCode()}.None, cancellationToken: {_cancellation!.Usage}).ConfigureAwait(false);");
        writer.Write($"    {Saga.Usage} = _cosmosResponse.Resource;");
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
        writer.BlankLine();
        writer.WriteComment("Try to load the existing saga document from CosmosDB");
        // Declare as mutable so it can be assigned in either the try or the with branch.
        writer.Write($"let mutable {Saga.FSharpUsage} = Unchecked.defaultof<{Saga.VariableType.FSharpName()}>");
        writer.Write("BLOCK:try");
        writer.Write(
            $"let! _cosmosResponse = {_container!.FSharpUsage}.ReadItemAsync<{Saga.VariableType.FSharpName()}>({_sagaId.FSharpUsage}, {typeof(PartitionKey).FSharpName()}.None, cancellationToken = {_cancellation!.FSharpUsage})");
        writer.Write($"{Saga.FSharpUsage} <- _cosmosResponse.Resource");
        writer.FinishBlock();
        // Single-case with; guard ensures non-404 CosmosExceptions propagate.
        writer.Write(
            $"BLOCK:with :? {typeof(CosmosException).FSharpName()} as e when e.StatusCode = System.Net.HttpStatusCode.NotFound ->");
        writer.Write($"{Saga.FSharpUsage} <- Unchecked.defaultof<{Saga.VariableType.FSharpName()}>");
        writer.FinishBlock();

        Next?.GenerateFSharpCode(method, writer);
    }
}
