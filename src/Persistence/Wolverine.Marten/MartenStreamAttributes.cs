using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Marten;

/// <summary>
/// The Marten spelling of <see cref="StreamStateAttribute" />: read a stream's <see cref="StreamState" />
/// through Marten specifically.
/// </summary>
/// <remarks>
/// GH-3627. Prefer <c>[StreamState]</c> in new code — it works against any event store integration. This
/// exists for the same reason <see cref="ReadAggregateAttribute" /> does: it <b>names</b> its store rather
/// than resolving one, so it still works in a host that called <c>AddMarten(...)</c> without
/// <c>IntegrateWithWolverine()</c>, where nothing ever registers a persistence strategy.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class MartenStreamStateAttribute : StreamStateAttribute
{
    public MartenStreamStateAttribute()
    {
    }

    public MartenStreamStateAttribute(string argumentName) : base(argumentName)
    {
    }

    protected override IEventSourcingFrameProvider ResolveProvider(IChain chain, GenerationRules rules,
        IServiceContainer container) => new MartenPersistenceFrameProvider();
}

/// <summary>
/// The Marten spelling of <see cref="StreamEventsAttribute" />. See
/// <see cref="MartenStreamStateAttribute" /> for why it exists.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class MartenStreamEventsAttribute : StreamEventsAttribute
{
    public MartenStreamEventsAttribute()
    {
    }

    public MartenStreamEventsAttribute(string argumentName) : base(argumentName)
    {
    }

    protected override IEventSourcingFrameProvider ResolveProvider(IChain chain, GenerationRules rules,
        IServiceContainer container) => new MartenPersistenceFrameProvider();
}
