using System.Diagnostics.CodeAnalysis;

namespace Wolverine.RoutingSlip;

internal static class RoutingSlipExtensions
{
    public static bool TryGetExecutedActivity(this RoutingSlip routingSlip, [MaybeNullWhen(false)] out RoutingSlipExecutionLog executionLog)
    {
        return routingSlip.ExecutedActivities.TryPop(out executionLog);
    }
    
    public static bool TryGetRemainingActivity(this RoutingSlip routingSlip, [MaybeNullWhen(false)] out RoutingSlipExecution execution)
    {
        return routingSlip.RemainingActivities.TryDequeue(out execution);
    }

    public static void AddExecutedActivity(this RoutingSlip routingSlip, RoutingSlipExecutionLog executionLog)
    {
        routingSlip.ExecutedActivities.Push(executionLog);
    }
}