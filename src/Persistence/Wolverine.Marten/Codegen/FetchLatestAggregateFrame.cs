using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Marten;
using Marten.Services.BatchQuerying;

namespace Wolverine.Marten.Codegen;

// GH-3907: unchanged, but it moved out of ReadAggregateAttribute.cs when that attribute became a shell
// over Wolverine core's [ReadModel]. The attribute no longer names this type - it reaches it through
// IEventSourcingFrameProvider.BuildFetchLatestFrame, which is what keeps the batch-query enlistment
// below on Marten's side of the seam.
internal class FetchLatestAggregateFrame : AsyncFrame, IBatchableFrame
{
    private readonly Variable _identity;
    private Variable _session = null!;
    private Variable _token = null!;
    private Variable _batchQuery = null!;
    private Variable _batchQueryItem = null!;

    public FetchLatestAggregateFrame(Type aggregateType, Variable identity)
    {
        if (identity.VariableType == typeof(Guid) || identity.VariableType == typeof(string))
        {
            _identity = identity;
        }
        else
        {
            var valueType = ValueTypeInfo.ForType(identity.VariableType);
            _identity = new MemberAccessVariable(identity, valueType.ValueProperty);
        }

        Aggregate = new Variable(aggregateType, this);
    }

    public Variable Aggregate { get; }

    public void WriteCodeToEnlistInBatchQuery(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
            throw new InvalidOperationException("This frame has not been enlisted in a MartenBatchFrame");

        writer.Write(
            $"var {_batchQueryItem.Usage} = {_batchQuery!.Usage}.Events.{nameof(IBatchEvents.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage});");
    }

    public void EnlistInBatchQuery(Variable batchQuery)
    {
        _batchQueryItem = new Variable(typeof(Task<>).MakeGenericType(Aggregate.VariableType), Aggregate.Usage + "_BatchItem",
            this);
        _batchQuery = batchQuery;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        if (_batchQuery != null)
        {
            yield return _batchQuery;
        }

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_batchQueryItem == null)
        {
            writer.Write($"var {Aggregate.Usage} = await {_session.Usage}.Events.{nameof(IEventStoreOperations.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        }
        else
        {
            writer.Write(
                $"var {Aggregate.Usage} = await {_batchQueryItem.Usage}.ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }
}
