using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat.Persistence.Sagas;

namespace Wolverine.Polecat;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as being part of the
///     Polecat Dynamic Consistency Boundary (DCB) workflow.
/// </summary>
/// <remarks>
///     GH-3911: the workflow itself is <see cref="DcbModelAttribute" /> in Wolverine core now, and works
///     the same against any event store integration. This is the Polecat spelling of it, kept because it
///     is what existing code says. Prefer <c>[DcbModel]</c> in new code.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class BoundaryModelAttribute : DcbModelAttribute
{
    // GH-3907: name the store rather than resolving one. AddPolecat without IntegrateWithWolverine()
    // registers no persistence strategy, and this attribute has always worked in that configuration.
    protected override IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return new PolecatPersistenceFrameProvider();
    }
}
