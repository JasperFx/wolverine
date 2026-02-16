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
}
