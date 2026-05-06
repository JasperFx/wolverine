using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.HealthChecks;

/// <summary>
/// Registration helpers for Wolverine's ASP.NET Core <see cref="IHealthCheck"/>
/// integrations. Mirrors the conventional <c>AddX</c> shape used by other
/// <see cref="IHealthChecksBuilder"/> extension packages.
/// </summary>
public static class HealthCheckBuilderExtensions
{
    /// <summary>
    /// Register a <see cref="WolverineBusHealthCheck"/> with the supplied
    /// <see cref="IHealthChecksBuilder"/>. The check is healthy once the
    /// Wolverine runtime has finished starting up and the runtime cancellation
    /// token has not been signalled.
    /// </summary>
    /// <param name="builder">The health-checks builder.</param>
    /// <param name="name">Health-check name. Defaults to <c>"wolverine"</c>.</param>
    /// <param name="failureStatus">Status reported when the check fails. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags. Useful for splitting Kubernetes liveness vs. readiness probes (e.g. <c>"ready"</c>, <c>"live"</c>).</param>
    /// <param name="timeout">Optional per-check timeout.</param>
    public static IHealthChecksBuilder AddWolverine(
        this IHealthChecksBuilder builder,
        string name = "wolverine",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new WolverineBusHealthCheck(sp.GetRequiredService<IWolverineRuntime>()),
            failureStatus,
            tags,
            timeout));
    }

    /// <summary>
    /// Register a <see cref="WolverineListenerHealthCheck"/> with the supplied
    /// <see cref="IHealthChecksBuilder"/>. The check inspects each listener
    /// returned by <c>IWolverineRuntime.Endpoints.ActiveListeners()</c>.
    /// </summary>
    /// <param name="builder">The health-checks builder.</param>
    /// <param name="name">Health-check name. Defaults to <c>"wolverine-listeners"</c>.</param>
    /// <param name="filter">Optional predicate used to scope the check (e.g. by listener name, URI scheme, or endpoint role). When omitted every active listener is included.</param>
    /// <param name="failureStatus">Status reported when the check fails. Defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional tags. Useful for splitting Kubernetes liveness vs. readiness probes.</param>
    /// <param name="timeout">Optional per-check timeout.</param>
    public static IHealthChecksBuilder AddWolverineListeners(
        this IHealthChecksBuilder builder,
        string name = "wolverine-listeners",
        Func<IListeningAgent, bool>? filter = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new WolverineListenerHealthCheck(sp.GetRequiredService<IWolverineRuntime>(), filter),
            failureStatus,
            tags,
            timeout));
    }
}
