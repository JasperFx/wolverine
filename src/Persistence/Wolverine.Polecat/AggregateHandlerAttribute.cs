using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Polecat;

/// <summary>
///     Applies middleware to Wolverine message actions to apply a workflow with concurrency protections for
///     "command" messages that use a Polecat projected aggregate to "decide" what
///     on new events to persist to the aggregate stream.
/// </summary>
/// <remarks>
///     GH-3907: the workflow itself is <see cref="DeciderFunctionAttribute" /> in Wolverine core now, and
///     works the same against any event store integration. This is the Polecat spelling of it, kept because
///     it is what existing code says. Prefer <c>[DeciderFunction]</c> in new code — the name says what the
///     method is (<c>decide(command, state) -&gt; events</c>) rather than which store it reads from.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AggregateHandlerAttribute : DeciderFunctionAttribute
{
    // The two ConcurrencyStyle enums are the same two members in the same order. This ctor exists so
    // that [AggregateHandler(ConcurrencyStyle.Exclusive)] written against Wolverine.Polecat's spelling
    // keeps compiling.
    public AggregateHandlerAttribute(ConcurrencyStyle loadStyle) : base((ModelConcurrencyStyle)(int)loadStyle)
    {
    }

    public AggregateHandlerAttribute() : base(ModelConcurrencyStyle.Optimistic)
    {
    }

    // GH-3907: name the store rather than resolving one. AddMarten/AddPolecat without
    // IntegrateWithWolverine() registers no persistence strategy, and this attribute has always
    // worked in that configuration.
    protected override IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return new PolecatPersistenceFrameProvider();
    }
}
