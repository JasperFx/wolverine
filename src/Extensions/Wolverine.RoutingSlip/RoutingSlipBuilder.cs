using Wolverine.RoutingSlip.Internal;

namespace Wolverine.RoutingSlip;

/// <summary>
///     A builder for creating routing slips
/// </summary>
public sealed class RoutingSlipBuilder
{
    private readonly List<RoutingSlipExecution> _activities = [];
    
    /// <summary>
    ///     Add an activity to the routing slip
    /// </summary>
    /// <param name="name"></param>
    /// <param name="destinationUri"></param>
    public void AddActivity(string name, Uri destinationUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(destinationUri);
        
        var activity = new RoutingSlipExecution(name, destinationUri);
        _activities.Add(activity);
    }

    /// <summary>
    ///     Build the routing slip
    /// </summary>
    /// <returns></returns>
    public RoutingSlip Build()
    {
        return new RoutingSlip(Guid.NewGuid(), DateTime.UtcNow,
            new Queue<RoutingSlipExecution>(_activities), new SerializableStack<RoutingSlipExecutionLog>());
    }
}