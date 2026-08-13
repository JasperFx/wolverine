using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Fisher.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Fisher;

/// <summary>
///     Marks a parameter to a Wolverine message handler or HTTP endpoint method as being part of the
///     Fisher Dynamic Consistency Boundary (DCB) workflow. The handler must have a Load/Before method
///     that returns an <see cref="JasperFx.Events.Tags.EventTagQuery" />. Wolverine will call
///     <c>IDocumentSession.Events.FetchForWritingByTags&lt;T&gt;(query)</c> and project the matching
///     events into the parameter type. Return values from the handler are appended via
///     <see cref="JasperFx.Events.Tags.IEventBoundary{T}.AppendOne" />.
/// </summary>
/// <remarks>
///     GH-3911: the workflow itself is <see cref="DcbModelAttribute" /> in Wolverine core, and works the
///     same against any event store integration. This is the Fisher spelling of it, kept so the three
///     integrations read alike. Prefer <c>[DcbModel]</c> in new code.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class BoundaryModelAttribute : DcbModelAttribute
{
    // GH-3907: name the store rather than resolving one. AddFisher without IntegrateWithWolverine()
    // registers no persistence strategy, and the store-named attributes have always worked there.
    protected override IEventSourcingFrameProvider ResolveEventSourcingProvider(GenerationRules rules,
        IServiceContainer container, Type modelType)
    {
        return new FisherPersistenceFrameProvider();
    }
}
