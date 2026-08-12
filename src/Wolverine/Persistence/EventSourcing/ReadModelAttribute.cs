using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as an event sourced model
///     that the handler only <b>reads</b>: the aggregate is resolved through the store's
///     <c>FetchLatest()</c> API, with no stream lock and no expectation that the handler appends events.
/// </summary>
/// <remarks>
///     <para>
///     Store-agnostic — it works against whichever event store integration claims the aggregate type. Use
///     <see cref="WriteModelAttribute" /> instead when the handler is going to emit events.
///     </para>
///     <para>
///     <c>Wolverine.Marten.ReadAggregateAttribute</c> and <c>Wolverine.Polecat.ReadAggregateAttribute</c>
///     both derive from this and are kept as-is for existing code. GH-3907.
///     </para>
/// </remarks>
public class ReadModelAttribute : WolverineParameterAttribute, IDataRequirement, IRefersToAggregate
{
    private OnMissing? _onMissing;

    public ReadModelAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public ReadModelAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    /// Is the existence of this aggregate required for the rest of the handler action or HTTP endpoint
    /// execution to continue? Default is true.
    /// </summary>
    public bool Required { get; set; } = true;

    public string MissingMessage { get; set; } = null!;

    public OnMissing OnMissing
    {
        get => _onMissing ?? OnMissing.Simple404;
        set => _onMissing = value;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        _onMissing ??= container.GetInstance<WolverineOptions>().EntityDefaults.OnMissing;

        var provider = rules.FindEventSourcingFrameProvider(container, parameter.ParameterType);

        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = ((IPersistenceFrameProvider)provider).DetermineSagaIdType(parameter.ParameterType, container);

        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            // Fall back to strong typed ID matching
            identity = tryFindStrongTypedIdentityVariable(chain, parameter.ParameterType, idType);
            if (identity == null)
            {
                throw new InvalidEntityLoadUsageException(this, parameter);
            }
        }

        // The store builds this frame: core never writes "session.Events.FetchLatest<T>(...)" itself,
        // which is what keeps Marten's batch-query enlistment on Marten's side of the seam. GH-3907.
        var frame = provider.BuildFetchLatestFrame(parameter.ParameterType, identity);
        var aggregate = FindAggregateVariable(frame, parameter.ParameterType, provider);
        aggregate.OverrideName(parameter.Name!);

        Variable returnVariable;
        if (Required)
        {
            var otherFrames = chain.AddStopConditionIfNull(aggregate, identity, this);

            var block = new LoadEntityFrameBlock(aggregate, otherFrames);
            chain.Middleware.Add(block);

            returnVariable = block.Mirror;
        }
        else
        {
            chain.Middleware.Add(frame);
            returnVariable = aggregate;
        }

        // Store deferred assignment for middleware methods added later (Before/After)
        AggregateHandling.StoreDeferredMiddlewareVariable(chain, parameter.Name!, returnVariable);

        return returnVariable;
    }

    private static Variable FindAggregateVariable(Frame frame, Type aggregateType,
        IEventSourcingFrameProvider provider)
    {
        var aggregate = frame.Creates.FirstOrDefault(x => x.VariableType == aggregateType);

        if (aggregate == null)
        {
            throw new InvalidOperationException(
                $"The {provider.StoreName} integration's {frame.GetType().FullNameInCode()} did not create a variable of type " +
                $"{aggregateType.FullNameInCode()}. {nameof(IEventSourcingFrameProvider)}.{nameof(IEventSourcingFrameProvider.BuildFetchLatestFrame)} " +
                "must return a frame that creates exactly one variable of the aggregate type.");
        }

        return aggregate;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler/command/model types come from handler discovery, which already roots them; this is the dynamic codegen path. See docs/guide/aot.md.")]
    private Variable? tryFindStrongTypedIdentityVariable(IChain chain, Type aggregateType, Type idType)
    {
        var strongTypedIdType = idType;

        if (WriteModelAttribute.IsPrimitiveIdType(idType))
        {
            strongTypedIdType = WriteModelAttribute.FindIdentifiedByType(aggregateType);
        }

        if (strongTypedIdType == null || WriteModelAttribute.IsPrimitiveIdType(strongTypedIdType)) return null;

        var inputType = chain.InputType();
        if (inputType == null) return null;

        var matchingProps = inputType.GetProperties()
            .Where(x => x.PropertyType == strongTypedIdType && x.CanRead)
            .ToArray();

        if (matchingProps.Length == 1)
        {
            if (chain.TryFindVariable(matchingProps[0].Name, ValueSource, strongTypedIdType, out var variable))
            {
                return variable;
            }
        }

        return null;
    }
}
