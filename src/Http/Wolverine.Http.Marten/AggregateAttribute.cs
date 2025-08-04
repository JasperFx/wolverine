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