using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Polecat;

/// <summary>
///     Marks a parameter to a Wolverine HTTP endpoint or message handler method as being part of the Polecat event sourcing
///     "aggregate handler" workflow
/// </summary>
/// <remarks>
///     GH-3907: the workflow itself is <see cref="WriteModelAttribute" /> in Wolverine core now, and works
///     the same against any event store integration. This is the Polecat spelling of it, kept because it is
///     what existing code says. Prefer <c>[WriteModel]</c> in new code.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class WriteAggregateAttribute : WriteModelAttribute
{
    public WriteAggregateAttribute()
    {
    }

    public WriteAggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }

    /// <summary>
    ///     Opt into exclusive locking or optimistic checks on the aggregate stream
    ///     version. Default is Optimistic
    /// </summary>
    /// <remarks>
    ///     Shadows <see cref="WriteModelAttribute.LoadStyle" /> only so that
    ///     <c>[WriteAggregate(LoadStyle = ConcurrencyStyle.Exclusive)]</c> written against
    ///     <see cref="Wolverine.Polecat.ConcurrencyStyle" /> keeps compiling. The two enums are the same two
    ///     members in the same order; this forwards to the one the workflow actually reads.
    /// </remarks>
    public new ConcurrencyStyle LoadStyle
    {
        get => (ConcurrencyStyle)(int)base.LoadStyle;
        set => base.LoadStyle = (ModelConcurrencyStyle)(int)value;
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
