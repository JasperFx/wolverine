# Exclusive Node Processing

Sometimes you need to ensure that only one node in your cluster processes messages from a specific queue or topic, but you still want to take advantage of parallel processing for better throughput. This is different from strict ordering, which processes messages one at a time.

## When to Use Exclusive Node Processing

Use exclusive node processing when you need:

- **Singleton processing**: Background jobs or scheduled tasks that should only run on one node
- **Resource constraints**: Operations that access limited resources that can't be shared across nodes
- **Stateful processing**: When maintaining in-memory state that shouldn't be distributed
- **Ordered event streams**: Processing events in order while still maintaining throughput

## Basic Configuration

### Exclusive Node with Parallelism

Configure a listener to run exclusively on one node while processing multiple messages in parallel:

```cs
var builder = Host.CreateDefaultBuilder();
builder.UseWolverine(opts =>
{
    opts.ListenToRabbitQueue("important-jobs")
        .ExclusiveNodeWithParallelism(maxParallelism: 5);
});
```

This configuration ensures:
- Only one node in the cluster will process this queue
- Up to 5 messages can be processed in parallel on that node
- If the exclusive node fails, another node will take over

### Default Parallelism

If you don't specify the parallelism level, it defaults to 10:

```csharp
opts.ListenToRabbitQueue("background-tasks")
    .ExclusiveNodeWithParallelism(); // Defaults to 10 parallel messages
```

## Session-Based Ordering

For scenarios where you need to maintain ordering within specific groups (like Azure Service Bus sessions), use exclusive node with session ordering:

```cs
opts.ListenToAzureServiceBusQueue("ordered-events")
    .ExclusiveNodeWithSessionOrdering(maxParallelSessions: 5);
```

This ensures:
- Only one node processes the queue
- Multiple sessions can be processed in parallel (up to 5 in this example)
- Messages within each session are processed in order
- Different sessions can be processed concurrently

## Azure Service Bus Specific Configuration

Azure Service Bus has special support for exclusive node processing with sessions:

```cs
opts.ListenToAzureServiceBusQueue("user-events")
    .ExclusiveNodeWithSessions(maxParallelSessions: 8);
```

This is a convenience method that:
1. Enables session support with the specified parallelism
2. Configures exclusive node processing
3. Ensures proper session handling

For topic subscriptions without sessions:

```cs
opts.ListenToAzureServiceBusSubscription("notifications", "email-sender")
    .ExclusiveNodeWithParallelism(maxParallelism: 3);
```

## Combining with Other Options

Exclusive node processing can be combined with other listener configurations:

```cs
opts.ListenToRabbitQueue("critical-tasks")
    .ExclusiveNodeWithParallelism(maxParallelism: 5)
    .UseDurableInbox()              // Use durable inbox for reliability
    .TelemetryEnabled(true)         // Enable telemetry
    .Named("CriticalTaskProcessor"); // Give it a friendly name
```

## Comparison with Other Modes

| Mode | Nodes | Parallelism | Ordering | Use Case |
|------|-------|-------------|----------|----------|
| **Default (Competing Consumers)** | All nodes | Configurable | No guarantee | High throughput, load balancing |
| **Sequential** | Current node | 1 | Yes (local) | Local ordering, single thread |
| **ListenWithStrictOrdering** | One (exclusive) | 1 | Yes (global) | Global ordering, single thread |
| **ExclusiveNodeWithParallelism** | One (exclusive) | Configurable | No | Singleton with throughput |
| **ExclusiveNodeWithSessionOrdering** | One (exclusive) | Configurable | Yes (per session) | Singleton with session ordering |

## Implementation Notes

### Leader Election

When using exclusive node processing, Wolverine uses its leader election mechanism to ensure only one node claims the exclusive listener. This requires:

1. A persistence layer (SQL Server, PostgreSQL, or RavenDB)
2. Node agent support enabled

```cs
opts.PersistMessagesWithSqlServer(connectionString)
    .EnableNodeAgentSupport(); // Required for leader election

opts.ListenToRabbitQueue("singleton-queue")
    .ExclusiveNodeWithParallelism(5);
```

### Failover Behavior

If the node running an exclusive listener fails:

1. Other nodes detect the failure through the persistence layer
2. A new node is elected to take over the exclusive listener
3. Processing resumes on the new node
4. Any in-flight messages are handled according to your durability settings

### Local Queues

Exclusive node processing is not supported for local queues since they are inherently single-node:

```cs
// This will throw NotSupportedException
opts.LocalQueue("local")
    .ExclusiveNodeWithParallelism(5); // ❌ Not supported
```

## Testing Exclusive Node Processing

When testing exclusive node processing:

1. **Unit Tests**: Test the configuration separately from the execution
2. **Integration Tests**: Use `DurabilityMode.Solo` to simplify testing
3. **Load Tests**: Verify that parallelism improves throughput as expected

```cs
// In tests, use Solo mode to avoid leader election complexity
opts.Durability.Mode = DurabilityMode.Solo;

opts.ListenToRabbitQueue("test-queue")
    .ExclusiveNodeWithParallelism(5);
```

## Performance Considerations

- **Parallelism Level**: Set based on your message processing time and resource constraints
- **Session Count**: For session-based ordering, balance between parallelism and memory usage
- **Failover Time**: Leader election typically takes a few seconds; plan accordingly
- **Message Distribution**: Ensure your message grouping (sessions) distributes evenly for best performance
- **Resource Implications**: Higher parallelism values increase memory usage and thread pool consumption. Each parallel message processor maintains its own execution context. For CPU-bound operations, setting parallelism higher than available CPU cores may decrease performance. For I/O-bound operations, higher values can improve throughput but monitor memory usage carefully.

## Troubleshooting

### Messages Not Processing

If messages aren't being processed:
1. Check that node agents are enabled
2. Verify the persistence layer is configured
3. Look for leader election errors in logs
4. Ensure only one node is claiming the exclusive listener

### Lower Than Expected Throughput

If throughput is lower than expected:
1. Increase the parallelism level
2. Check for blocking operations in message handlers
3. Verify that sessions (if used) are well-distributed
4. Monitor CPU and memory usage on the exclusive node

### Failover Not Working

If failover isn't working properly:
1. Check network connectivity between nodes
2. Verify all nodes can access the persistence layer
3. Look for timeout or deadlock issues in logs
4. Ensure node agent support is enabled on all nodes
