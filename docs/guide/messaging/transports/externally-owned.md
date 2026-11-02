# Externally Owned Broker Objects <Badge type="tip" text="6.62" />

Plenty of systems use queues, topics or exchanges that belong to someone else. A service listens to a topic that
the publishing service owns, or sends to a queue another team manages, and the identity it connects with may not
even have the permissions to create or delete those objects. You still want `AutoProvision()` for everything your
application *does* own, but `AutoProvision()` is all or nothing for a transport, and turning it off to protect the
objects you don't own means losing it for the ones you do.

Marking an endpoint as **externally owned** settles that one endpoint at a time. Wolverine *uses* the broker
object behind it, but:

* never creates it at startup, even with `AutoProvision()` on
* never creates it in `resources setup`
* never deletes it in `resources teardown`

while everything else in the same transport is provisioned and torn down as usual.

`resources check` still checks that an externally owned object exists. Your application still needs it to be
there, so a missing one is still worth failing a deploy gate over. When a transport finds an externally owned object
missing as it starts, it throws an `ExternallyOwnedObjectNotFoundException` that names the object, the same
exception on every transport.

Being externally owned is about the object, not its contents. Purging a queue at startup with `AutoPurgeOnStartup()`
or `AutoPurgeAllQueues()` still purges an externally owned queue. Redis is the exception: purging a Redis stream
deletes it, so Wolverine never purges an externally owned stream.

::: tip
Creating an object you don't own isn't harmless even when you have the permissions. Wolverine creates it with its
own default options rather than the ones its owner intended, and some broker settings can never be changed once the
object exists, Azure Service Bus partitioning and duplicate detection among them. If your service deploys before
the owning service, the owner is left deleting and recreating the object to fix it, along with everything else
already using it.
:::

## Transport Support

The flag lives on every Wolverine endpoint as `Endpoint.IsExternallyOwned`, and each transport exposes it through
its own fluent configuration, usually as `ExternallyOwned()`:

| Transport | Usage | Since |
|-----------|-------|-------|
| [Rabbit MQ](/guide/messaging/transports/rabbitmq/object-management#externally-owned-queues-and-exchanges) | `ExternallyOwned()` on queue listeners, queue subscribers and exchange subscribers | 6.6 |
| [Kafka](/guide/messaging/transports/kafka#externally-owned-topics) | `ExternallyOwned()` on topic listeners, topic subscribers and topic group listeners | 6.7 |
| [Azure Service Bus](/guide/messaging/transports/azureservicebus/topics#externally-owned-topics-queues-and-subscriptions) | `ExternallyOwned()` on queues, topics and subscriptions, and `TopicIsExternallyOwned()` on a subscription listener | 6.62 |
| [Amazon SQS](/guide/messaging/transports/sqs/queues#externally-owned-queues) | `ExternallyOwned()` on queue listeners and subscribers | 6.62 |
| [Amazon SNS](/guide/messaging/transports/sns#externally-owned-topics) | `ExternallyOwned()` on topic subscribers. Queues subscribed with `SubscribeSqsQueue()` are still subscribed | 6.62 |
| [Google Pub/Sub](/guide/messaging/transports/gcp-pubsub/listening#externally-owned-topics-and-subscriptions) | `ExternallyOwned()` on topic listeners and subscribers, and `TopicIsExternallyOwned()` on a listener | 6.62 |
| [NATS](/guide/messaging/transports/nats#externally-owned-streams-and-consumers) | `ExternallyOwned()` on listeners and subscribers, and `StreamIsExternallyOwned()` on a listener | 6.62 |
| [Redis](/guide/messaging/transports/redis#externally-owned-streams-and-consumer-groups) | `ExternallyOwned()` on stream listeners and subscribers, and `StreamIsExternallyOwned()` on a listener | 6.62 |
| Database queues: [PostgreSQL](/guide/durability/postgresql#externally-owned-queues), [SQL Server](/guide/durability/sqlserver#externally-owned-queues), [MySQL](/guide/durability/mysql#externally-owned-queues), [Oracle](/guide/durability/oracle#externally-owned-queues), [SQLite](/guide/durability/sqlite#externally-owned-queues), and the NServiceBus and MassTransit interop queues | `ExternallyOwned()` on queue listeners and subscribers. The queue tables are left out of the message store's schema migrations too | 6.62 |

## Externally Owned or AutoProvision Off?

Turn `AutoProvision()` off when your application owns *none* of its broker objects, or when something else, such
as infrastructure as code, provisions all of them. Mark endpoints externally owned when the application owns some
objects and not others. The two compose: with `AutoProvision()` off, an externally owned endpoint is still skipped
by `resources setup` and `resources teardown`, which `AutoProvision()` has no say over.

## For Transport Authors

A transport built on `BrokerTransport<TEndpoint>` gets most of this for free. The broker resource behind
`resources setup` and `resources teardown` skips any endpoint whose `IsExternallyOwned` is `true`, so only the
startup path needs the transport's attention. Where a transport provisions an endpoint as it starts, it should ask
`BrokerTransport.ShouldAutoProvision(endpoint)`, which is `AutoProvision && !endpoint.IsExternallyOwned`, rather than
read `AutoProvision` alone.
