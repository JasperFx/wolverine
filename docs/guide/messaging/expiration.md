# Message Expiration

Some messages you publish or send will be transient, or only be valid for only a brief time. In this case
you may find it valuable to apply message expiration rules to tell Wolverine to ignore messages that are
too old.

You won't use this explicitly very often, but this information is ultimately stored on the Wolverine `Envelope` with
this property:

snippet: sample_envelope_deliver_by_property

At runtime, Wolverine will:

1. Wolverine will discard received messages that are past their `DeliverBy` time 
2. Wolverine will also discard outgoing messages that are past their `DeliverBy` time
3. For transports that support this (Rabbit MQ for example), Wolverine will try to pass the `DeliverBy` time into a transport's native message expiration capabilities

## At Message Sending Time

On a message by message basis, you can explicitly set the deliver by time either as an absolute time or as a `TimeSpan` past now
with this syntax:

snippet: sample_message_expiration_by_message

## By Subscriber

The message expiration can be set as a rule for all messages sent to a specific subscribing endpoint as shown by
this sample:

snippet: sample_delivery_expiration_rules_per_subscriber

## By Message Type

At the message type level, you can set message expiration rules with the `Wolverine.Attributes.DeliverWithinAttribute` attribute
on the message type as in this sample:

snippet: sample_using_deliver_within_attribute