using Wolverine.Marten;

namespace Wolverine.Http.Marten;

/// <summary>
///     Marks a parameter to an HTTP endpoint as being part of the Marten event sourcing
///     "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class AggregateAttribute : WriteAggregateAttribute
{
    public AggregateAttribute()
    {
    }

    public AggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
    }
}

/// <summary>
///     <see cref="AggregateAttribute" /> loading the aggregate by its <typeparamref name="TNaturalKey" /> natural key.
///     Equivalent to <c>[Aggregate(NaturalKey = typeof(TNaturalKey))]</c>. GH-5947.
/// </summary>
/// <typeparam name="TNaturalKey">
///     The aggregate's natural key type. Never <c>TKey?</c>: a <c>TKey?</c> member is still matched to <c>TKey</c>.
/// </typeparam>
[AttributeUsage(AttributeTargets.Parameter)]
public class AggregateAttribute<TNaturalKey> : AggregateAttribute where TNaturalKey : notnull
{
    public AggregateAttribute()
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }

    public AggregateAttribute(string? routeOrParameterName) : base(routeOrParameterName)
    {
        NaturalKeyTypeArgument = typeof(TNaturalKey);
    }
}
