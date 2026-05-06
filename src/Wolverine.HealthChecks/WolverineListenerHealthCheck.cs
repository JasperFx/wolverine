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
/// Reports the state of Wolverine's active listening agents as an ASP.NET Core
/// <see cref="IHealthCheck"/>.
///
/// Status mapping:
/// <list type="bullet">
///   <item><description><c>Healthy</c> — every listener is <see cref="ListeningStatus.Accepting"/>.</description></item>
///   <item><description><c>Degraded</c> — at least one listener is <see cref="ListeningStatus.TooBusy"/> or <see cref="ListeningStatus.GloballyLatched"/>.</description></item>
///   <item><description><c>Unhealthy</c> — every listener is <see cref="ListeningStatus.Stopped"/>, or no listeners exist when one is expected.</description></item>
/// </list>
///
/// Pass an optional <c>filter</c> when constructing the check to scope it down to
/// a subset of listeners (e.g. by name, tag, or URI scheme). When the filter
/// excludes every listener the check reports <c>Healthy</c> with a note rather
/// than failing — register a separate check per scope if you want a missing
/// listener for that scope to fail.
/// </summary>
public sealed class WolverineListenerHealthCheck : IHealthCheck
{
    private readonly IWolverineRuntime _runtime;
    private readonly Func<IListeningAgent, bool>? _filter;

    public WolverineListenerHealthCheck(IWolverineRuntime runtime, Func<IListeningAgent, bool>? filter = null)
    {
        _runtime = runtime;
        _filter = filter;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<IListeningAgent> listeners;
        try
        {
            var all = _runtime.Endpoints.ActiveListeners();
            listeners = (_filter is null ? all : all.Where(_filter)).ToList();
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "Could not enumerate Wolverine active listeners.",
                exception: ex));
        }

        var counts = new Dictionary<ListeningStatus, int>
        {
            [ListeningStatus.Accepting] = 0,
            [ListeningStatus.TooBusy] = 0,
            [ListeningStatus.Stopped] = 0,
            [ListeningStatus.Unknown] = 0,
            [ListeningStatus.GloballyLatched] = 0
        };

        var perListener = new Dictionary<string, object>();
        foreach (var listener in listeners)
        {
            counts[listener.Status]++;
            perListener[listener.Uri.ToString()] = listener.Status.ToString();
        }

        var data = new Dictionary<string, object>
        {
            ["listenerCount"] = listeners.Count,
            ["accepting"] = counts[ListeningStatus.Accepting],
            ["tooBusy"] = counts[ListeningStatus.TooBusy],
            ["stopped"] = counts[ListeningStatus.Stopped],
            ["unknown"] = counts[ListeningStatus.Unknown],
            ["globallyLatched"] = counts[ListeningStatus.GloballyLatched],
            ["listeners"] = perListener
        };

        if (listeners.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                description: "No active Wolverine listeners matched the configured scope.",
                data: data));
        }

        if (counts[ListeningStatus.Stopped] == listeners.Count)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                description: "All Wolverine listeners are stopped.",
                data: data));
        }

        if (counts[ListeningStatus.TooBusy] > 0 || counts[ListeningStatus.GloballyLatched] > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                description: "One or more Wolverine listeners are too busy or latched.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            description: "All Wolverine listeners are accepting messages.",
            data: data));
    }
}
