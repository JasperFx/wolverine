using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.HealthChecks;

/// <summary>
/// Reports the connection state of every Wolverine transport that implements
/// <see cref="ITransport.BuildHealthCheck"/> (RabbitMQ, Kafka, Azure Service Bus, Amazon SQS, NATS,
/// Redis, Pulsar, MQTT...) as one ASP.NET Core <see cref="IHealthCheck"/>. The worst transport status
/// wins: one <see cref="TransportHealthStatus.Unhealthy"/> transport fails the check, one
/// <see cref="TransportHealthStatus.Degraded"/> transport degrades it. Transports without a health check
/// (local queues, TCP...) are not reported. Before the Wolverine runtime has started, the check fails
/// with the registration's failure status, so it belongs on a readiness probe, not a liveness probe.
/// </summary>
public sealed class WolverineTransportsHealthCheck : IHealthCheck
{
    private readonly IWolverineRuntime _runtime;
    private IReadOnlyList<WolverineTransportHealthCheck>? _checks;

    public WolverineTransportsHealthCheck(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _runtime.AssertHasStarted();
        }
        catch (WolverineHasNotStartedException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus,
                "Wolverine runtime has not finished starting yet, so its transports are not connected.", ex);
        }

        // Transports are only known once the runtime has started, and a transport's health check reads the
        // state the transport already holds, so build them once and reuse them
        var checks = _checks ??= _runtime.Options.Transports
            .Select(t => t.BuildHealthCheck(_runtime))
            .OfType<WolverineTransportHealthCheck>()
            .ToList();

        var data = new Dictionary<string, object>();
        if (checks.Count == 0)
        {
            return HealthCheckResult.Healthy("No Wolverine transport reports its health.", data);
        }

        var worst = TransportHealthStatus.Healthy;
        var messages = new List<string>();
        foreach (var check in checks)
        {
            TransportHealthResult result;
            try
            {
                result = await check.CheckHealthAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = new TransportHealthResult(check.TransportName, check.Protocol,
                    TransportHealthStatus.Unhealthy, e.Message, DateTimeOffset.UtcNow);
            }

            var key = $"{result.Protocol}:{result.TransportName}";
            data[key] = result.Status.ToString();
            if (result.Status > worst) worst = result.Status;
            if (result.Status != TransportHealthStatus.Healthy)
            {
                messages.Add($"{key} is {result.Status}{(result.Message is null ? "" : ": " + result.Message)}");
            }
        }

        return worst switch
        {
            TransportHealthStatus.Healthy => HealthCheckResult.Healthy("All Wolverine transports are healthy.", data),
            TransportHealthStatus.Degraded => HealthCheckResult.Degraded(string.Join("; ", messages), data: data),
            _ => new HealthCheckResult(context.Registration.FailureStatus, string.Join("; ", messages), data: data)
        };
    }
}
