namespace Wolverine.RoutingSlip;

/// <summary>
/// A builder for creating routing slips.
/// </summary>
public sealed class RoutingSlipBuilder
{
    private readonly List<RoutingSlipExecution> _activities = [];

    /// <summary>
    /// Add an activity to the routing slip.
    /// </summary>
    public void AddActivity(string name, Uri destinationUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(destinationUri);

        _activities.Add(new RoutingSlipExecution(name, destinationUri));
    }

    /// <summary>
    /// Build the routing slip.
    /// </summary>
    public RoutingSlip Build()
    {
        return new RoutingSlip(Guid.NewGuid(), DateTime.UtcNow, _activities, Array.Empty<RoutingSlipExecutionLog>());
    }
}
