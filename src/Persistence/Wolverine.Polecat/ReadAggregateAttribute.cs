using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Polecat;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Polecat;

/// <summary>
/// Use Polecat's FetchLatest() API to retrieve the parameter value
/// </summary>
public class ReadAggregateAttribute : WolverineParameterAttribute, IDataRequirement, IRefersToAggregate
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
        var idType = new PolecatPersistenceFrameProvider().DetermineSagaIdType(parameter.ParameterType, container);

        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            identity = tryFindStrongTypedIdentityVariable(chain, parameter.ParameterType, idType);
            if (identity == null)
            {
                throw new InvalidEntityLoadUsageException(this, parameter);
            }
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

        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name, returnVariable);

        return returnVariable;
    }

    private Variable? tryFindStrongTypedIdentityVariable(IChain chain, Type aggregateType, Type idType)
    {
        var strongTypedIdType = idType;

        if (WriteAggregateAttribute.IsPrimitiveIdType(idType))
        {
            strongTypedIdType = WriteAggregateAttribute.FindIdentifiedByType(aggregateType);
        }

        if (strongTypedIdType == null || WriteAggregateAttribute.IsPrimitiveIdType(strongTypedIdType)) return null;

        var inputType = chain.InputType();
        if (inputType == null) return null;

        var matchingProps = inputType.GetProperties()
            .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
            .ToArray();

        if (matchingProps.Length == 1)
        {
            if (chain.TryFindVariable(matchingProps[0].Name, ValueSource, strongTypedIdType, out var variable))
                return variable;
        }

        return null;
    }
}

internal class FetchLatestAggregateFrame : AsyncFrame
{
    private readonly Variable _identity;
    private Variable _session;
    private Variable _token;

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
        writer.Write($"var {Aggregate.Usage} = await {_session.Usage}.Events.FetchLatest<{Aggregate.VariableType.FullNameInCode()}>({_identity.Usage}, {_token.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
