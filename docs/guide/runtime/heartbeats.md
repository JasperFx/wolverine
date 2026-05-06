# Heartbeats

Wolverine can periodically emit a `WolverineHeartbeat` message from each running
node so that external monitoring tools (for example
[CritterWatch](https://github.com/JasperFx/CritterWatch)) can detect when a node
goes dark. Heartbeats are off by default and are opted-in through
`EnableHeartbeats`.

## Quickstart

```csharp
using Wolverine;

await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Enable with the default 30-second cadence
        opts.EnableHeartbeats();

        // ...or override the interval
        // opts.EnableHeartbeats(TimeSpan.FromSeconds(10));

        // Route heartbeats wherever the dashboard listens. Without a publish
        // rule the heartbeat is local-only and does nothing if no in-process
        // handler subscribes.
        opts.PublishMessage<Wolverine.Runtime.Heartbeat.WolverineHeartbeat>()
            .ToRabbitExchange("monitoring");
    })
    .RunOasisAsync();
```

## What gets sent

Each heartbeat carries the bare minimum a monitor needs to attribute it back to
a node:

| Field         | Source                                               |
| ------------- | ---------------------------------------------------- |
| `ServiceName` | `WolverineOptions.ServiceName`                        |
| `NodeNumber`  | `WolverineOptions.Durability.AssignedNodeNumber`      |
| `SentAt`      | UTC timestamp captured at publish                    |
| `Uptime`      | Elapsed time since the heartbeat service started     |

The publish goes through Wolverine's normal routing pipeline — apply
`PublishMessage`, `Publish().To*`, or any other publish rule the same way you
would for any application event.

## Configuration

`HeartbeatPolicy` lives at `WolverineOptions.Heartbeat`:

```csharp
opts.Heartbeat.Enabled = false;        // disable without removing registration
opts.Heartbeat.Interval = 5.Seconds(); // override the cadence
```

`Enabled = false` causes the hosted service to exit at startup, which is the
recommended way to suppress heartbeats per environment (e.g. local development)
without altering the registration.

## Where this fits

Heartbeats answer "is this node still alive?" but say nothing about whether
listeners or transports are healthy. Pair them with the existing
[durability and node health](../durability/leadership-and-troubleshooting.md)
features for a fuller monitoring story.
