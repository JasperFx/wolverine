# ASP.NET Core Health Checks

The `WolverineFx.HealthChecks` package adds Wolverine-aware
[`IHealthCheck`](https://learn.microsoft.com/aspnet/core/host-and-deploy/health-checks)
implementations so you can plug Wolverine straight into the standard
ASP.NET Core health-check pipeline. Two checks ship in the box:

| Check | What it covers |
| --- | --- |
| `WolverineBusHealthCheck` | The Wolverine runtime itself — has it started? Is it cancelling? |
| `WolverineListenerHealthCheck` | Listening agents — are they accepting, too busy, latched, or stopped? |

Per-broker connectivity checks (RabbitMQ, Azure Service Bus, etc.) are
intentionally out of scope here and will be addressed by transport-specific
health-check helpers.

## Install

```bash
dotnet add package WolverineFx.HealthChecks
```

## Register

Both checks are registered through the conventional `IHealthChecksBuilder`
extension shape, just like any other ASP.NET Core health check:

```csharp
using Wolverine.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWolverine();

builder.Services.AddHealthChecks()
    .AddWolverine()           // bus check, name: "wolverine"
    .AddWolverineListeners(); // listener check, name: "wolverine-listeners"

var app = builder.Build();

app.MapHealthChecks("/health");

app.Run();
```

## Kubernetes liveness vs. readiness

Health checks accept the standard `tags` parameter, which means you can split
Kubernetes liveness and readiness probes the same way you would with any other
ASP.NET Core check. A typical pattern: the bus check feeds both probes, the
listener check feeds only readiness.

```csharp
builder.Services.AddHealthChecks()
    .AddWolverine(tags: new[] { "live", "ready" })
    .AddWolverineListeners(tags: new[] { "ready" });

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

## Listener status mapping

The listener check walks `IWolverineRuntime.Endpoints.ActiveListeners()` and
maps the per-listener status to the overall health-check status:

| Listener mix | Reported status |
| --- | --- |
| All `Accepting` | `Healthy` |
| Any `TooBusy` or `GloballyLatched` | `Degraded` |
| All `Stopped` | `Unhealthy` (uses the registered failure status) |
| No listeners match the configured filter | `Healthy` (with a note) |

Per-listener status counts and a URI-keyed map are surfaced through
`HealthCheckResult.Data` so dashboards (CritterWatch, Grafana, etc.) can render
detail without needing a second probe.

## Filtering listeners

`AddWolverineListeners` accepts an optional `filter` predicate so you can scope
the check by listener name, URI scheme, or any property exposed by
`IListeningAgent`. This is useful when you only care about a subset of
transports for a given probe — for example, requiring the inbound RabbitMQ
listeners to be up for readiness while ignoring local in-memory queues:

```csharp
builder.Services.AddHealthChecks()
    .AddWolverineListeners(
        name: "wolverine-rabbitmq-listeners",
        filter: agent => agent.Uri.Scheme == "rabbitmq",
        tags: new[] { "ready" });
```

If you want a missing scope to fail (rather than report healthy with a note),
register a separate check per scope and let the absence of any matching
listener bubble up as an explicit failure on that probe.
