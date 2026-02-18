using System.Diagnostics;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Services.BatchQuerying;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Use Marten's FetchLatest() API to retrieve the parameter value
/// </summary>
public class ReadAggregateAttribute : WolverineParameterAttribute, IDataRequirement
{
    private OnMissing? _onMissing;

    public ReadAggregateAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public ReadAggregateAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    /// Is the existence of this aggregate required for the rest of the handler action or HTTP endpoint
    /// execution to continue? Default is true.
    /// </summary>
    public bool Required { get; set; } = true;

    public string MissingMessage { get; set; }

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container, GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;
        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = new MartenPersistenceFrameProvider().DetermineSagaIdType(parameter.ParameterType, container);

        if (chain.ToString() == "GET_sti_aggregate_id")
        {
            Debug.WriteLine("Here");
        }
        
        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        var frame = new FetchLatestAggregateFrame(parameter.ParameterType, identity);
        frame.Aggregate.OverrideName(parameter.Name);

        Variable returnVariable;
        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(frame.Aggregate, identity, this);

            var block = new LoadEntityFrameBlock(frame.Aggregate, otherFrames);
            chain.Middleware.Add(block);

            returnVariable = block.Mirror;
        }
        else
        {
            chain.Middleware.Add(frame);
            returnVariable = frame.Aggregate;
        }

        // Store deferred assignment for middleware methods added later (Before/After)
        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name, returnVariable);

        return returnVariable;
    }
}

internal class FetchLatestAggregateFrame : AsyncFrame, IBatchableFrame
{
    private readonly Variable _identity;
    private Variable _session;
    private Variable _token;
    private Variable _batchQuery;
    private Variable _batchQueryItem;

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