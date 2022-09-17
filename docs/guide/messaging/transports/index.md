# Messaging Transports

In Wolverine parlance, a "transport" refers to one of Wolverine's adapter libraries that enable the usage of an
external messaging infrastructure technology like Rabbit MQ or Pulsar. The local queues and [lightweight TCP transport](/tcp)
come in the box with Wolverine, but you'll need an add on Nuget to enable any of the other transports.

## Key Abstractions

| Abstraction  | Description                                                                                                                                                                                                                                                 |
|--------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ITransport` | Manages the connection to the messaging infrastructure like a Rabbit MQ broker and creates all the other objects referenced below                                                                                                                           |
| `Endpoint`   | The configuration for a sending or receiving address to your transport identified by a unique Uri scheme. For example, a Rabbit MQ endpoint may refer to a queue or an exchange and binding key. A TCP endpoint will refer to a server name and port number |
| `IListener`  | A service that helps read messages from the underlying message transport and relays those to Wolverine as Wolverine's `Envelope` structure                                                                                                                        |
| `ISender`    | A service that helps put Wolverine `Envelope` structures out into the outgoing messaging infrastructure                                                                                                                                                        |

To build a new transport, we recommend looking first at the [Wolverine.Pulsar](https://github.com/JasperFx/wolverine/tree/master/src/Wolverine.Pulsar) library
for a sample. At a bare minimum, you'll need to implement the services above, and also add some kind of `WolverineOptions.Use[TransportName]()` extension
method to configure the connectivity to the messaging infrastructure and add the new transport to your Wolverine application.

Also note, you will definitely want to use the [SendingCompliance](https://github.com/JasperFx/wolverine/blob/master/src/TestingSupport/Compliance/SendingCompliance.cs)
tests in Wolverine to verify that your new transport meets all Wolverine requirements.
