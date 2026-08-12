using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as being part of the
///     Marten Dynamic Consistency Boundary (DCB) workflow. The handler must have a Load/Before method
///     that returns an <see cref="JasperFx.Events.Tags.EventTagQuery" />. Wolverine will call
///     <c>IDocumentSession.Events.FetchForWritingByTags&lt;T&gt;(query)</c> and project the matching
///     events into the parameter type. Return values from the handler are appended via
///     <see cref="JasperFx.Events.Tags.IEventBoundary{T}.AppendOne" />.
/// </summary>
/// <remarks>
///     GH-3911: the workflow itself is <see cref="DcbModelAttribute" /> in Wolverine core now, and works
///     the same against any event store integration. This is the Marten spelling of it, kept because it is
///     what existing code says. Prefer <c>[DcbModel]</c> in new code.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class BoundaryModelAttribute : DcbModelAttribute
{
    // GH-3907: name the store rather than resolving one. AddMarten without IntegrateWithWolverine()
    // registers no persistence strategy, and this attribute has always worked in that configuration.
    protected override IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return new MartenPersistenceFrameProvider();
    }
}
