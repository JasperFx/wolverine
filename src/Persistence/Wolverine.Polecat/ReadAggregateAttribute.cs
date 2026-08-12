using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Polecat;

/// <summary>
/// Use Polecat's FetchLatest() API to retrieve the parameter value
/// </summary>
/// <remarks>
///     GH-3907: the workflow itself is <see cref="ReadModelAttribute" /> in Wolverine core now, and works
///     the same against any event store integration. This is the Polecat spelling of it, kept because it is
///     what existing code says. Prefer <c>[ReadModel]</c> in new code.
/// </remarks>
public class ReadAggregateAttribute : ReadModelAttribute
{
    public ReadAggregateAttribute()
    {
    }

    public ReadAggregateAttribute(string argumentName) : base(argumentName)
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
