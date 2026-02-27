namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
///     Executes the next activity in a routing slip
/// </summary>
public interface IActivityExecutor
{
    /// <summary>
    ///     Execute the next activity in a routing slip
    /// </summary>
    /// <param name="context"></param>
    /// <param name="slip"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecution next);

    /// <summary>
    ///     Compensate the next activity in a routing slip
    /// </summary>
    /// <param name="context"></param>
    /// <param name="slip"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    ValueTask CompensateAsync(IMessageContext context, RoutingSlip slip, RoutingSlipExecutionLog next);
}