# Broadcast Messages to Every Node <Badge type="tip" text="6.50" />

Most of the time, Wolverine messaging is all about *competing consumers* -- you send a message, and exactly
one running node in your cluster picks it up and handles it. Every so often though, you'll have a message that
*every* node in the cluster needs to handle. Maybe you need to throw away an in-memory cache on every node
after some reference data changed, push out a configuration change, or reset some kind of node-local state.

For that, Wolverine has the `IMessageBus.BroadcastToAllNodesAsync()` method:

<!-- snippet: sample_broadcast_to_all_nodes -->
<a id='snippet-sample_broadcast_to_all_nodes'></a>
```cs
public record InvalidateCache(string Region);

public static class InvalidateCacheHandler
{
    // Runs once on every node in the cluster when the message is broadcast
    // with IMessageBus.BroadcastToAllNodesAsync()
    public static void Handle(InvalidateCache message)
    {
        // Evict this node's in-memory copy of the cached region
    }
}

public static class BroadcastingSample
{
    public static async Task CatalogChanged(IMessageBus bus)
    {
        // Every node in the cluster, including this one, gets a copy
        // of the message and handles it once
        await bus.BroadcastToAllNodesAsync(new InvalidateCache("products"));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/broadcast_to_all_nodes.cs#L146-L170' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_broadcast_to_all_nodes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When you call `BroadcastToAllNodesAsync()`, Wolverine:

1. Hands the message to the current node's own local queue, where it's handled by your normal message handler
   for that message type
2. Looks up every other active node in the cluster from Wolverine's node storage, and sends a copy of the
   message to each of those nodes through its node control endpoint. That's either a dedicated queue from your
   message broker if you're using something like `EnableWolverineControlQueues()` with Rabbit MQ, or Wolverine's
   own database-backed control queue otherwise

The end result is that every node, including the one that sent it, handles the message exactly once.

::: info
The current node does *not* go through the normal `PublishAsync()` routing for the broadcast message. If your
message type is also routed to some other endpoint like a Rabbit MQ queue, a broadcast won't send it there.
Wolverine just runs the handler on every node.
:::

::: tip
If you call `BroadcastToAllNodesAsync()` from inside a message handler that's using Wolverine's transactional
outbox, the broadcast messages aren't sent until the transaction commits, just like any other outgoing message.
That's usually exactly what you want for something like cache invalidation.
:::

## Solo Mode and Other Non-Clustered Modes

Broadcasting to the other nodes only applies when Wolverine is running in the default `Balanced` durability
mode. In `Solo` or `Serverless` mode, there's only one node to worry about, so `BroadcastToAllNodesAsync()`
just has the current node handle the message. Like every other messaging operation, it isn't allowed at all
in `MediatorOnly` mode.

## Testing

`TestMessageContext` records broadcast messages in its `BroadcastToAllNodes` collection (they're also included
in `AllOutgoing`), so you can unit test a handler that broadcasts the same way you'd test one that publishes.

::: warning
Messages sent to node control endpoints are not visible to Wolverine's [tracked sessions](/guide/testing#integration-testing-with-tracked-sessions),
so an integration test that verifies delivery to other nodes has to poll for the expected outcome.
:::

## Caveats

There are a few things to know before you lean on this too hard:

* Delivery to the other nodes is fire and forget. The node control endpoints aren't durable, and there's no
  acknowledgement or aggregation of results from the other nodes
* A node that doesn't have a handler for the message type -- say an older version of your application during
  a blue/green deployment -- will quietly discard the message, as node control endpoints don't have a dead
  letter queue
* Wolverine reads the current list of nodes from storage on every call. That's perfectly fine for occasional
  broadcasts, but don't put `BroadcastToAllNodesAsync()` in a hot loop
* Nodes that have stopped sending heartbeats are skipped, using the same `DurabilitySettings.StaleNodeTimeout`
  that Wolverine's leader uses to detect dead nodes
