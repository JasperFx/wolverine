using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Use Marten's FetchLatest() API to retrieve the parameter value
/// </summary>
public class ReadAggregateAttribute : WolverineParameterAttribute, IDataRequirement
{
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
    public OnMissing OnMissing { get; set; }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container, GenerationRules rules)
    {
        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = new MartenPersistenceFrameProvider().DetermineSagaIdType(parameter.ParameterType, container);
        
        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        var frame = new FetchLatestAggregateFrame(parameter.ParameterType, identity);
        
        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(frame.Aggregate, identity, this);
            
            var block = new LoadEntityFrameBlock(frame.Aggregate, otherFrames);
            chain.Middleware.Add(block);

            return block.Mirror;
        }
        
        chain.Middleware.Add(frame);

        return frame.Aggregate;
    }
}

internal class FetchLatestAggregateFrame : AsyncFrame
{
    private readonly Variable _identity;
    private Variable _session;
    private Variable _token;

    public FetchLatestAggregateFrame(Type aggregateType, Variable identity)
    {
        _identity = identity;
        Aggregate = new Variable(aggregateType, this);
    }

    public Variable Aggregate { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _token = chain.FindVariable(typeof(CancellationToken));
        yield return _token;

        yield return _identity;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Aggregate.Usage} = await {_session.Usage}.Events.{nameof(IEventStoreOperations.FetchLatest)}<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }
}