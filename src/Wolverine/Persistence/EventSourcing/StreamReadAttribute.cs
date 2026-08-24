using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Shared base for the raw stream reads — <see cref="StreamStateAttribute" /> and
///     <see cref="StreamEventsAttribute" />. GH-3627.
/// </summary>
/// <remarks>
///     <para>
///     Store-agnostic, and unusually so: the parameter types these bind are <b>already</b> shared
///     vocabulary — <c>JasperFx.Events.StreamState</c> and <c>JasperFx.Events.IEvent</c> — and
///     <c>FetchStreamStateAsync</c> / <c>FetchStreamAsync</c> are declared on
///     <c>JasperFx.Events.IQueryEventStore</c>. Only the <c>.Events</c> accessor that reaches them is
///     store-specific, which is exactly what <see cref="IEventSourcingFrameProvider" /> exists to keep on
///     the store's side of the seam.
///     </para>
///     <para>
///     That shared vocabulary is also what makes provider resolution different from
///     <see cref="ReadModelAttribute" />. There is no aggregate type here to ask "who owns this?" about —
///     the whole point of reading raw stream state is that the aggregate type is a <i>runtime</i> fact —
///     so resolution goes: an explicit <see cref="AggregateType" />, then the ancillary store a
///     <see cref="StorageAttribute" /> routed the chain to, then the single registered event sourcing
///     integration. Ambiguity is an error naming both escape hatches rather than a guess.
///     </para>
/// </remarks>
public abstract class StreamReadAttribute : WolverineParameterAttribute
{
    protected StreamReadAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    protected StreamReadAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    ///     Optionally name the aggregate whose stream this is, purely to identify the owning store in an
    ///     application with more than one event sourcing integration. It does not change what is read.
    /// </summary>
    public Type? AggregateType { get; set; }

    /// <summary>
    ///     Find the event sourcing integration that should serve this read. See the class remarks for why
    ///     this cannot simply key off the parameter type the way <see cref="ReadModelAttribute" /> does.
    /// </summary>
    protected virtual IEventSourcingFrameProvider ResolveProvider(IChain chain, GenerationRules rules,
        IServiceContainer container)
    {
        // 1. The author named the aggregate, so the question is already answered
        if (AggregateType != null)
        {
            return rules.FindEventSourcingFrameProvider(container, AggregateType);
        }

        // 2. The chain was routed to an ancillary store by [Storage(typeof(IMyStore))]. Reading the
        //    primary store's events from a handler explicitly pointed at a secondary one would be wrong
        //    in a way nothing downstream could detect
        if (chain.AncillaryStoreType is { } storeType)
        {
            var ancillary = container.GetAllInstances<IAncillaryStoreFrameProvider>()
                .FirstOrDefault(x => x.Matches(storeType));

            if (ancillary?.EventSourcing is { } fromStore)
            {
                return fromStore;
            }

            throw new InvalidOperationException(
                $"This handler is routed to the ancillary store '{storeType.FullNameInCode()}', but no registered " +
                $"Wolverine persistence integration owns that store as an event store. [{Label()}] cannot determine " +
                "which event store to read from.");
        }

        // 3. One integration registered, so there is nothing to be ambiguous about
        var providers = rules.OrderedPersistenceProviders()
            .OfType<IEventSourcingFrameProvider>()
            .ToArray();

        if (providers.Length == 1)
        {
            return providers[0];
        }

        if (providers.Length == 0)
        {
            throw new InvalidOperationException(
                $"[{Label()}] requires an event store integration. Add Wolverine.Marten, Wolverine.Polecat, or " +
                "another integration that implements IEventSourcingFrameProvider, and register it with " +
                "IntegrateWithWolverine().");
        }

        throw new InvalidOperationException(
            $"[{Label()}] cannot tell which of {providers.Length} registered event store integrations " +
            $"({providers.Select(x => x.StoreName).Join(", ")}) this stream belongs to, because the parameter type " +
            "is store-agnostic and carries no aggregate type. Say which one by setting AggregateType — " +
            $"[{Label()}(AggregateType = typeof(MyAggregate))] — or route the handler to a specific store with " +
            "[Storage(typeof(IMyStore))].");
    }

    private string Label() => GetType().Name.Replace("Attribute", "");

    /// <summary>Build the store's frame for this read, and name the variable it creates.</summary>
    protected abstract Frame BuildFrame(IEventSourcingFrameProvider provider, Variable identity);

    /// <summary>The type the store's frame is required to create exactly one variable of.</summary>
    protected abstract Type ReadType { get; }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        var provider = ResolveProvider(chain, rules, container);

        // Stream identity is the store's, not an aggregate's: Guid or string, per the integration
        var idType = provider.DetermineStreamIdentity(container) == JasperFx.Events.StreamIdentity.AsGuid
            ? typeof(Guid)
            : typeof(string);

        if (!tryFindIdentityVariable(chain, parameter, idType, out var identity))
        {
            throw new InvalidEntityLoadUsageException(this, parameter);
        }

        var frame = BuildFrame(provider, identity!);

        var created = frame.Creates.FirstOrDefault(x => x.VariableType == ReadType);
        if (created == null)
        {
            throw new InvalidOperationException(
                $"The {provider.StoreName} integration's {frame.GetType().FullNameInCode()} did not create a variable " +
                $"of type {ReadType.FullNameInCode()}.");
        }

        created.OverrideName(parameter.Name!);

        return ModifyForRead(chain, parameter, frame, created, identity!);
    }

    /// <summary>
    ///     What each subclass does with the frame once it exists — a null guard for
    ///     <see cref="StreamStateAttribute" />, nothing for <see cref="StreamEventsAttribute" />.
    /// </summary>
    protected abstract Variable ModifyForRead(IChain chain, ParameterInfo parameter, Frame frame, Variable created,
        Variable identity);
}
