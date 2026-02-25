using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip;

/// <inheritdoc />
public sealed class RoutingSlip : IRoutingSlip
{
    private readonly List<RoutingSlipExecution> _remainingActivities;
    private readonly List<RoutingSlipExecutionLog> _executedActivities;

    [JsonConstructor]
    public RoutingSlip(Guid trackingNumber, DateTime createTimestamp,
        IReadOnlyList<RoutingSlipExecution>? remainingActivities,
        IReadOnlyList<RoutingSlipExecutionLog>? executedActivities)
    {
        TrackingNumber = trackingNumber;
        CreateTimestamp = createTimestamp;
        _remainingActivities = remainingActivities?.ToList() ?? [];
        _executedActivities = executedActivities?.ToList() ?? [];
    }

    public Guid TrackingNumber { get; }

    public DateTime CreateTimestamp { get; }

    public IReadOnlyList<RoutingSlipExecution> RemainingActivities => _remainingActivities;

    public IReadOnlyList<RoutingSlipExecutionLog> ExecutedActivities => _executedActivities;

    public bool TryTakeNextActivity([MaybeNullWhen(false)] out RoutingSlipExecution execution)
    {
        if (_remainingActivities.Count == 0)
        {
            execution = null;
            return false;
        }

        execution = _remainingActivities[0];
        _remainingActivities.RemoveAt(0);
        return true;
    }

    public void MarkActivityExecuted(Guid executionId, RoutingSlipExecution activity)
    {
        _executedActivities.Add(new RoutingSlipExecutionLog(executionId, activity.Name, activity.DestinationUri));
    }

    public bool TryTakeLastExecutedActivity([MaybeNullWhen(false)] out RoutingSlipExecutionLog executionLog)
    {
        if (_executedActivities.Count == 0)
        {
            executionLog = null;
            return false;
        }

        var index = _executedActivities.Count - 1;
        executionLog = _executedActivities[index];
        _executedActivities.RemoveAt(index);
        return true;
    }
}
