# Wolverine as Messaging Bus

::: tip
While today it is perfectly possible to use multiple transport types in a single Wolverine application, each
separate transport can only connect to a single message broker. This may change in the future depending
on user demand.
:::

There's certainly some value in Wolverine just being a command bus running inside of a single process, but now
it's time to utilize Wolverine to both publish and process messages received through external infrastructure like [Rabbit MQ](https://www.rabbitmq.com/)
or [Pulsar](https://pulsar.apache.org/).

## Terminology

To put this into perspective, here's how a Wolverine application could be connected to the outside world:

![Wolverine Messaging Architecture](/WolverineMessaging.png)

:::tip
The diagram above should just say "Message Handler" as Wolverine makes no structural differentiation between commands or events, but Jeremy is being too lazy to fix the diagram.
:::

Before going into any kind of detail about how to use Wolverine messaging, let's talk about some terminology:

* *Transport* -- This refers to the support within Wolverine for external messaging infrastructure tools like Rabbit MQ or Pulsar
* *Endpoint* -- A Wolverine connection to some sort of external resource like a Rabbit MQ exchange or an Amazon SQS queue. The [Async API](https://www.asyncapi.com/) specification refers to this as a *channel*, and Wolverine may very well change its nomenclature in the future to be consistent with Async API
* *Sending Agent* -- You won't use this directly in your own code, but Wolverine's internal adapters to publish outgoing messages to transport endpoints
* *Listening Agent* -- Again, an internal detail of Wolverine that receives messages from external transport endpoints, and mediates between the transports and executing the message handlers
* *Message Store* -- Database storage for Wolverine's [inbox/outbox persistent messaging](/guide/persistence/)
* *Durability Agent* -- An internal subsystem in Wolverine that runs in a background service to interact with the message store for Wolverine's [transactional inbox/outbox](https://microservices.io/patterns/data/transactional-outbox.html) functionality
* *Message* -- Typically just a .NET class or C# record that can be easily serialized. See [messages and serialization](/guide/messages) for more information
* *Message Handler* -- A method or function that "knows" how to process an incoming message. See [Message Handlers](/guide/handlers/) for more information

To get started with messaging, first add a transport (or use the built in TCP transport):

* Rabbit MQ
* AWS SQS
* Azure Service Bus
* TCP Sockets
* Pulsar (experimental)
