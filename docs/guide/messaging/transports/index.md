# Messaging Transports

## Community Transports

Community-built transports maintained outside this repository:

* [Salesforce Pub/Sub](https://github.com/meyc-v1/wolverinefxcontrib-salesforcepubsub) — listen to
  Salesforce platform events (topics, custom channels, and managed event subscriptions) as ordinary
  Wolverine messages

## Broker Startup Retries <Badge type="tip" text="6.31" />

When a Wolverine host starts, every broker transport connects and provisions whatever it was told to
auto-provision. A failed attempt is retried every five seconds, up to twenty attempts, until a wall-clock
budget elapses:

```csharp
builder.Host.UseWolverine(opts =>
{
    // The default. Startup gives up after this and throws a BrokerInitializationException
    // carrying the last underlying failure.
    opts.BrokerInitializationTimeout = 2.Minutes();
});
```

The budget is checked *between* attempts. Broker client SDKs do not accept a `CancellationToken` on their
provisioning calls, so an attempt already in flight is never interrupted and the real worst case is the
budget plus one client request timeout.

Before 6.31 there was no clock here at all — only the count of twenty attempts. Because a single attempt
costs whatever the broker client's own request timeout is (60 seconds for librdkafka, which backs the Kafka
transport), an unreachable or degraded broker made host startup take **21 minutes and 38 seconds** to fail,
measured. That is longer than most orchestrator start probes will wait, and it produced a hang rather than
the error that was already available. Raise the budget if your broker is genuinely slow to accept
connections at deploy time; lower it if you would rather fail fast and let your orchestrator restart the
process.

## Building a new Transport

In Wolverine parlance, a "transport" refers to one of Wolverine's adapter libraries that enable the usage of an
external messaging infrastructure technology like Rabbit MQ or Pulsar. The local queues and [lightweight TCP transport](/tcp)
come in the box with Wolverine, but you'll need an add on Nuget to enable any of the other transports.

### Key Abstractions

| Abstraction  | Description                                                                                                                                                                                                                                                 |
|--------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ITransport` | Manages the connection to the messaging infrastructure like a Rabbit MQ broker and creates all the other objects referenced below                                                                                                                           |
| `Endpoint`   | The configuration for a sending or receiving address to your transport identified by a unique Uri scheme. For example, a Rabbit MQ endpoint may refer to a queue or an exchange and binding key. A TCP endpoint will refer to a server name and port number |
| `IListener`  | A service that helps read messages from the underlying message transport and relays those to Wolverine as Wolverine's `Envelope` structure                                                                                                                        |
| `ISender`    | A service that helps put Wolverine `Envelope` structures out into the outgoing messaging infrastructure                                                                                                                                                        |

To build a new transport, we recommend looking first at the [Wolverine.AmazonSqs](https://github.com/JasperFx/wolverine/tree/main/src/Wolverine.Pulsar) library
for a sample. At a bare minimum, you'll need to implement the services above, and also add some kind of `WolverineOptions.Use[TransportName]()` extension
method to configure the connectivity to the messaging infrastructure and add the new transport to your Wolverine application.

Also note, you will definitely want to use the [SendingCompliance](https://github.com/JasperFx/wolverine/blob/main/src/TestingSupport/Compliance/SendingCompliance.cs)
tests in Wolverine to verify that your new transport meets all Wolverine requirements.
