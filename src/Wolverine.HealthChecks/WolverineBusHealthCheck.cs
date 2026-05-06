using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Runtime;

namespace Wolverine.HealthChecks;

/// <summary>
/// Reports the high-level state of the Wolverine runtime as an ASP.NET Core
/// <see cref="IHealthCheck"/>. The check is healthy when the runtime has finished
/// starting up and the runtime cancellation token has not been signalled (which
/// happens when the host begins shutting down).
/// </summary>
public sealed class WolverineBusHealthCheck : IHealthCheck
{
    private readonly IWolverineRuntime _runtime;

    public WolverineBusHealthCheck(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["serviceName"] = _runtime.Options.ServiceName ?? string.Empty,
            ["uniqueNodeId"] = _runtime.Options.UniqueNodeId,
            ["cancellationRequested"] = _runtime.Cancellation.IsCancellationRequested
        };

        if (_runtime.Cancellation.IsCancellationRequested)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Wolverine runtime is shutting down (cancellation requested).",
                data: data));
        }

        try
        {
            _runtime.AssertHasStarted();
        }
        catch (WolverineHasNotStartedException ex)
        {
            data["started"] = false;
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Wolverine runtime has not finished starting yet.",
                exception: ex,
                data: data));
        }

        data["started"] = true;
        return Task.FromResult(HealthCheckResult.Healthy(
            description: "Wolverine runtime is started and accepting messages.",
            data: data));
    }
}
