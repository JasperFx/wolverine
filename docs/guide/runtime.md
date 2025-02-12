# Runtime Architecture

::: info
Wolverine makes absolutely no differentiation between logical [events and commands](https://codeopinion.com/commands-events-whats-the-difference) within your system. To Wolverine,
everything is just a message.
:::

The two key parts of a Wolverine application are messages:

<!-- snippet: sample_DebutAccount_command -->
<a id='snippet-sample_debutaccount_command'></a>
```cs
// A "command" message
public record DebitAccount(long AccountId, decimal Amount);

// An "event" message
public record AccountOverdrawn(long AccountId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L69-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debutaccount_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the message handling code for the messages, which in Wolverine's case just means a function or method that accepts the message type as its first argument like so:

<!-- snippet: sample_DebitAccountHandler -->
<a id='snippet-sample_debitaccounthandler'></a>
```cs
public static class DebitAccountHandler
{
    public static void Handle(DebitAccount account)
    {
        Console.WriteLine($"I'm supposed to debit {account.Amount} from account {account.AccountId}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L57-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debitaccounthandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Invoking a Message Inline

At runtime, you can use Wolverine to invoke the message handling for a message *inline* in the current executing thread with Wolverine effectively acting as a mediator:

![Invoke Wolverine Handler](/invoke-handler.png)

It's a bit more complicated than that though, as the inline invocation looks like this simplified sequence diagram:

![Invoke a Message Inline](/invoke-message-sequence-diagram.png)

As you can hopefully see, even the inline invocation is adding some value beyond merely "mediating" between the caller
and the actual message handler by:

1. Wrapping Open Telemetry tracing and execution metrics around the execution
2. Correlating the execution in logs to the original calling activity
3. Providing some inline retry [error handling policies](/guide/handlers/error-handling) for transient errors
4. Publishing [cascading messages](/guide/handlers/cascading) from the message execution only *after* the execution succeeds as an in memory outbox


## Asynchronous Messaging

::: info
You can, of course, happily publish messages to an external queue and consume those very same messages later in the
same process.
:::

Wolverine supports asynchronous messaging through both its [local, in-process queueing](/guide/messaging/transports/local) mechanism and through external
messaging brokers like Kafka, Rabbit MQ, Azure Service Bus, or Amazon SQS. The local queueing is a valuable way to add
background processing to a system, and can even be durably backed by a database with full-blown transactional inbox/outbox
support to retain in process work across unexpected system shutdowns or restarts. What the local queue cannot do is share
work across a cluster of running nodes. In other words, you will have to use external messaging brokers to achieve any
kind of [competing consumer](https://www.enterpriseintegrationpatterns.com/patterns/messaging/CompetingConsumers.html) work sharing for better scalability. 

::: info
Wolverine listening agents all support competing consumers out of the box for work distribution across a node cluster -- unless you are purposely opting into [strictly ordered listeners](/guide/messaging/listeners.html#strictly-ordered-listeners) where only one
node is allowed to handle messages from a given queue or subscription.
:::

The other main usage of Wolverine is to send messages from your current process to another process through some kind of external transport like a Rabbit MQ/Azure Service Bus/Amazon SQS queue and
have Wolverine execute that message in another process (or back to the original process):

![Send a Message](/sending-message.png)

The internals of publishing a message are shown in this simplified sequence diagram:

![Publish a Message](/publish-message-sequence-diagram.png)

Along the way, Wolverine has to:

1. Serialize the message body
2. Route the outgoing message to the proper subscriber(s)
3. Utilize any publishing rules like "this message should be discarded after 10 seconds"
4. Map the outgoing Wolverine `Envelope` representation of the message into whatever the underlying transport (Azure Service Bus et al.) uses
5. Actually invoke the actual messaging infrastructure to send out the message

On the flip side, listening for a message follows this sequence shown for the "happy path" of receiving a message through Rabbit MQ:

![Listen for a Message](/listen-for-message-sequence-diagram.png)

During the listening process, Wolverine has to:

1. Map the incoming Rabbit MQ message to Wolverine's own `Envelope` structure
2. Determine what the actual message type is based on the `Envelope` data
3. Find the correct executor strategy for the message type
4. Deserialize the raw message data to the actual message body
5. Call the inner message executor for that message type
6. Carry out quite a bit of Open Telemetry activity tracing, report metrics, and just plain logging
7. Evaluate any errors against the error handling policies of the application or the specific message type

## Endpoint Types

::: info
Not all transports support all three types of endpoint modes, and will helpfully assert when you try to choose
an invalid option.
:::

### Inline Endpoints

Wolverine endpoints come in three basic flavors, with the first being **Inline** endpoints:

<!-- snippet: sample_using_process_inline -->
<a id='snippet-sample_using_process_inline'></a>
```cs
// Configuring a Wolverine application to listen to
// an Azure Service Bus queue with the "Inline" mode
opts.ListenToAzureServiceBusQueue(queueName, q => q.Options.AutoDeleteOnIdle = 5.Minutes()).ProcessInline();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/InlineSendingAndReceivingCompliance.cs#L29-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_process_inline' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With inline endpoints, as the name implies, calling `IMessageBus.SendAsync()` immediately sends the message to the external
message broker. Likewise, messages received from an external message queue are processed inline before Wolverine acknowledges
to the message broker that the message is received.

![Inline Endpoints](/inline-endpoint.png)

In the absence of a durable inbox/outbox, using inline endpoints is "safer" in terms of guaranteed delivery. As you might 
think, using inline agents can bottle neck the message processing, but that can be alleviated by opting into parallel listeners.

### Buffered Endpoints

In the second **Buffered** option, messages are queued locally between the actual external broker and the Wolverine handlers or senders.

To opt into buffering, you use this syntax:

<!-- snippet: sample_buffered_in_memory -->
<a id='snippet-sample_buffered_in_memory'></a>
```cs
// I overrode the buffering limits just to show
// that they exist for "back pressure"
opts.ListenToAzureServiceBusQueue("incoming")
    .BufferedInMemory(new BufferingLimits(1000, 200));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L139-L146' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_buffered_in_memory' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At runtime, you have a local [TPL Dataflow queue](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) between the Wolverine callers and the broker:

![Buffered Endpoints](/buffered-endpoint.png)

On the listening side, buffered endpoints do support [back pressure](https://www.educative.io/answers/techniques-to-exert-back-pressure-in-distributed-systems) (of sorts) where Wolverine will stop the actual message 
listener if too many messages are queued in memory to avoid chewing up your application memory. In transports like Amazon SQS that only support batched
message sending or receiving, `Buffered` is the default mode as that facilitates message batching.

`Buffered` message sending and receiving can lead to higher throughput, and should be considered for cases where messages
are ephemeral or expire and throughput is more important than delivery guarantees. The downside is that messages in the 
in memory queues can be lost in the case of the application shutting down unexpectedly -- but Wolverine tries to "drain"
the in memory queues on normal application shutdown.

### Durable Endpoints

**Durable** endpoints behave like **buffered** endpoints, but also use the [durable inbox/outbox message storage](/guide/durability/) to create much
stronger guarantees about message delivery and processing. You will need to use `Durable` endpoints in order to truly
take advantage of the persistent outbox mechanism in Wolverine. To opt into making an endpoint durable, use this syntax:

<!-- snippet: sample_durable_endpoint -->
<a id='snippet-sample_durable_endpoint'></a>
```cs
// I overrode the buffering limits just to show
// that they exist for "back pressure"
opts.ListenToAzureServiceBusQueue("incoming")
    .UseDurableInbox(new BufferingLimits(1000, 200));

opts.PublishAllMessages().ToAzureServiceBusQueue("outgoing")
    .UseDurableOutbox();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L236-L246' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_durable_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or use policies to do this in one fell swoop (which may not be what you actually want, but you could do this!):

<!-- snippet: sample_all_outgoing_are_durable -->
<a id='snippet-sample_all_outgoing_are_durable'></a>
```cs
opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L149-L153' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_all_outgoing_are_durable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As shown below, the `Durable` endpoint option adds an extra step to the `Buffered` behavior to add database storage of the 
incoming and outgoing messages:

![Durable Endpoints](/durable-endpoints.png)

Outgoing messages are deleted in the durable outbox upon successful sending acknowledgements from the external broker. Likewise,
incoming messages are also deleted from the durable inbox upon successful message execution.

The `Durable` endpoint option makes Wolverine's [local queueing](/guide/messaging/transports/local) robust enough to use for cases where you need 
guaranteed processing of messages, but don't want to use an external broker.

## How Wolverine Calls Your Message Handlers

![A real wolverine](/real_wolverine.jpeg)

Wolverine is a little different animal from the tools with similar features in the .NET ecosystem (pun intended:). Instead of the typical strategy of
requiring you to implement an adapter interface of some sort in *your* code, Wolverine uses [dynamically generated code](./codegen) to "weave" its internal adapter code and 
even middleware around your message handler code. 

In ideal circumstances, Wolverine is able to completely remove the runtime usage of an IoC container for even better performance. The
end result is a runtime pipeline that is able to accomplish its tasks with potentially much less performance overhead than comparable .NET frameworks 
that depend on adapter interfaces and copious runtime usage of IoC containers.

See [Code Generation in Wolverine](/guide/codegen) for much more information about this model and how it relates to the execution pipeline.

## Nodes and Agents

![Nodes and Agents](/nodes-and-agents.png)

Wolverine has some ability to distribute "sticky" or stateful work across running nodes in your application. To do so, 
Wolverine tracks the running "nodes" (just means an executing instance of your Wolverine application) and elects a 
single leader to distribute and assign "agents" to the running "nodes". Wolverine has built in health monitoring that
can detect when any node is offline to redistribute working agents to other nodes. Wolverine is also able to "fail over" the
leader assignment to a different node if the original leader is determined to be down. Likewise, Wolverine will redistribute
running agent assignments when new nodes are brought online.

::: info
You will have to have some kind of durable message storage configured for your application for the leader election
and agent assignments to function.
:::

The stateful, running "agents" are exposed through an `IAgent`
interface like so:

<!-- snippet: sample_IAgent -->
<a id='snippet-sample_iagent'></a>
```cs
/// <summary>
///     Models a constantly running background process within a Wolverine
///     node cluster
/// </summary>
public interface IAgent : IHostedService
{
    /// <summary>
    ///     Unique identification for this agent within the Wolverine system
    /// </summary>
    Uri Uri { get; }
    
    AgentStatus Status { get; }
}

public class CompositeAgent : IAgent
{
    private readonly List<IAgent> _agents;
    public Uri Uri { get; }

    public CompositeAgent(Uri uri, IEnumerable<IAgent> agents)
    {
        Uri = uri;
        _agents = agents.ToList();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StartAsync(cancellationToken);
        }

        Status = AgentStatus.Started;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents)
        {
            await agent.StopAsync(cancellationToken);
        }

        Status = AgentStatus.Started;
    }

    public AgentStatus Status { get; private set; } = AgentStatus.Stopped;
}

public enum AgentStatus
{
    Started,
    Stopped,
    Paused
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Agents/IAgent.cs#L6-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iagent' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With related groups of agents built and assigned by IoC-registered implementations of this interface:

<!-- snippet: sample_IAgentFamily -->
<a id='snippet-sample_iagentfamily'></a>
```cs
/// <summary>
///     Pluggable model for managing the assignment and execution of stateful, "sticky"
///     background agents on the various nodes of a running Wolverine cluster
/// </summary>
public interface IAgentFamily
{
    /// <summary>
    ///     Uri scheme for this family of agents
    /// </summary>
    string Scheme { get; }

    /// <summary>
    ///     List of all the possible agents by their identity for this family of agents
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync();

    /// <summary>
    ///     Create or resolve the agent for this family
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="wolverineRuntime"></param>
    /// <returns></returns>
    ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime);

    /// <summary>
    ///     All supported agent uris by this node instance
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync();

    /// <summary>
    ///     Assign agents to the currently running nodes when new nodes are detected or existing
    ///     nodes are deactivated
    /// </summary>
    /// <param name="assignments"></param>
    /// <returns></returns>
    ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Runtime/Agents/IAgentFamily.cs#L16-L58' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iagentfamily' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Built in examples of the agent and agent family are:

* Wolverine's built-in durability agent to recover orphaned messages from nodes that are detected to be offline, with one agent per tenant database
* Wolverine uses the agent assignment for "exclusive" message listeners like the strictly ordered listener option
* The integrated Marten projection and subscription load distribution

## IoC Container Integration

::: info
Wolverine has been tested with both the built in `ServiceProvider` and [Lamar](https://jasperfx.github.io/lamar), which was originally built
specifically to support what ended up becoming Wolverine. The previous limitation to only supporting Lamar was lifted in Wolverine 3.0.
:::

Wolverine is a significantly different animal than other .NET frameworks, and uses the IoC container quite differently than most
.NET application frameworks. For the most part, Wolverine is looking at the IoC container registrations and trying to generate code
to mimic the IoC behavior in the message handler and HTTP endpoint adapters that Wolverine generates internally. The benefits of this model are:

* The pre-generated code can tell you a lot about how Wolverine is handling your code, including any registered middleware
* The fastest IoC container is no IoC container
* Less conditional logic at runtime 
* Much slimmer exception stack traces when things inevitably go wrong. Wolverine's predecessor tool ([FubuMVC](https://fubumvc.github.io)) use nested objects created on every request or message for its middleware strategy, and the exception messages coming out of handler code could be *epic* with a lot of middleware active.

The downside is that Wolverine does not play well with the kind of runtime IoC tricks
other frameworks rely on for passing state. For example, because Wolverine.HTTP does not use the ASP.Net Core request services
to build endpoint types and its dependencies at runtime, it's a little clumsier to pass state from ASP.Net Core middleware
written into scoped IoC services, with custom multi-tenancy approaches being the usual cause of this. Wolverine certainly has its
own multi-tenancy support, and we don't think this is really a serious problem for most usages, but it has caused friction for
some Wolverine users converting from other frameworks.
