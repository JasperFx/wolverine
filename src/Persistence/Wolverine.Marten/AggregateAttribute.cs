namespace Wolverine.Marten;

/// <summary>
/// Marks a parameter to an HTTP endpoint as being part of the Marten event sourcing
/// "aggregate handler" workflow
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class AggregateAttribute : Attribute
{
    public string? RouteOrParameterName { get; }

    public AggregateAttribute()
    {
    }

    /// <summary>
    /// Specify exactly the route or parameter name that has the
    /// identity for this aggregate argument
    /// </summary>
    /// <param name="routeOrParameterName"></param>
    public AggregateAttribute(string routeOrParameterName)
    {
        RouteOrParameterName = routeOrParameterName;
    }

    /// <summary>
    /// Opt into exclusive locking or optimistic checks on the aggregate stream
    /// version. Default is Optimistic
    /// </summary>
    public ConcurrencyStyle LoadStyle { get; set; } = ConcurrencyStyle.Optimistic;
}