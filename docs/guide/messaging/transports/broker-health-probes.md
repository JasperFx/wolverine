# Broker Health Probes

Wolverine exposes an optional, transport-implemented contract for monitoring the
underlying broker connection. It is intentionally distinct from the existing
`WolverineTransportHealthCheck` (which feeds the ASP.NET Core health-check
pipeline) -- the broker probe is meant for interactive monitoring layers like
[CritterWatch](https://github.com/JasperFx/critterwatch) that want to render
broker connectivity at a glance.

## The contract

```csharp
public interface IBrokerHealthProbe
{
    Task<BrokerHealthSnapshot> ProbeAsync(CancellationToken ct);
}

public record BrokerHealthSnapshot(
    Uri TransportUri,
    string TransportType,
    BrokerHealthStatus Status,           // Unknown / Healthy / Degraded / Unhealthy
    string? Description,
    string? CertificateExpiry,           // ISO-8601 if TLS configured
    int ReconnectAttempts,
    DateTimeOffset LastSuccessfulAt);
```

Probes are **non-destructive**: they do not reconnect, bounce the connection,
or mutate transport state. They inspect the connection the transport already
holds and return a snapshot. If the connection is currently down, the probe
returns `Unhealthy` rather than throwing.

## Discovering probes at runtime

Every `ITransport` registered with Wolverine is iterable via
`runtime.Options.Transports`. Probes are discovered by filtering that
collection:

```csharp
var snapshots = await Task.WhenAll(
    runtime.Options.Transports
        .OfType<IBrokerHealthProbe>()
        .Select(p => p.ProbeAsync(cancellationToken)));
```

A transport that does not implement `IBrokerHealthProbe` simply doesn't
contribute a snapshot.

## Status semantics

| Status      | When it's reported                                                  |
|-------------|---------------------------------------------------------------------|
| `Unknown`   | Transport hasn't connected yet (host hasn't started, or disabled).  |
| `Healthy`   | Connection open, no recent flap.                                    |
| `Degraded`  | Connection open, but there's been a recent reconnect.               |
| `Unhealthy` | Connection currently down.                                          |

`ReconnectAttempts` counts genuine recovery events -- it is **not** incremented
by the initial connect at host startup. `LastSuccessfulAt` is the timestamp of
the most recent successful connect (initial or recovered).

## Transport support

| Transport             | Probe support              |
|-----------------------|----------------------------|
| RabbitMQ              | Yes (since this release)   |
| Azure Service Bus     | Pending                    |
| Amazon SQS / SNS      | Pending                    |
| Apache Kafka          | Pending                    |
| Apache Pulsar         | Pending                    |

The follow-up transports will pattern-match the RabbitMQ implementation as
their underlying client SDKs are surveyed.

## RabbitMQ specifics

For RabbitMQ, the probe inspects:

- `IConnection.IsOpen` for both the listening and sending connections;
- the most recent `ShutdownEventArgs` on those connections (used as the
  `Description` when the connection is down);
- the auto-recovery `RecoverySucceededAsync` event (used to track
  `ReconnectAttempts`);
- `ConnectionFactory.Ssl.CertPath` (parsed for `NotAfter` when TLS is
  enabled).

A reconnect within the last two minutes flips `Status` from `Healthy` to
`Degraded`. After that window the connection is reported as `Healthy` again
even if the reconnect counter is non-zero.
