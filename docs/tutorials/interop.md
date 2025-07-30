# Interoperability with Non Wolverine Systems

It's a complicated world, Wolverine is a relative newcomer in the asynchronous messaging space in the .NET ecosystem,
and who knows what other systems on completely different technical platforms you might have going on. As Wolverine has
gained adoption, and as a prerequisite for other folks to even consider adopting Wolverine, we've had to improve Wolverine's 
ability to exchange messages with non-Wolverine systems. We hope this guide will answer any questions you might have
about how to leverage interoperability with Wolverine and non-Wolverine systems.

As is typical for messaging tools, Wolverine has an internal ["envelope wrapper"](https://www.enterpriseintegrationpatterns.com/patterns/messaging/EnvelopeWrapper.html) structure called `Envelope`
that holds the .NET message object and/or the binary representation of the message and all known metadata about the message like:

* Correlation information
* The [message type name for Wolverine](/guide/messages.html#message-type-name-or-alias)
* The number of attempts in case of failures
* When a message was originally sent
* The content type of any serialized data
* Topic name, group id, and deduplication id for transports that can use that information
* Information about expected replies and a `ReplyUri` that tells Wolverine where to send any responses to the current message
* Other headers

As you can probably imagine, Wolverine uses this structure all throughout its internals to handle, send, track, and otherwise
coordinate message processing. When using Wolverine with external transport brokers like Kafka, Pulsar, Google Pubsub,
or Rabbit MQ, Wolverine goes through a bi-directional mapping from whatever each broker's own representation of a "message"
is to Wolverine's own `Envelope` structure. Likewise, when Wolverine sends messages through an external messaging broker,
it's having to map its `Envelope` to the transport's outgoing message structure as shown below:

![Envelope Mapping](/envelope-mappers.png)

As you can probably surmise from the diagram, there's an important abstraction in Wolverine called an "envelope mapper"
that does the work of translating Wolverine's `Envelope` structure to and from each message broker's own model for messages.

MORE HERE!!!
