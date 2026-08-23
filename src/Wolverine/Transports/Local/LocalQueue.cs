using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

public class LocalQueue : Endpoint
{
    public LocalQueue(string name) : base($"local://{name}".ToUri(), EndpointRole.Application)
    {
        EndpointName = name.ToLowerInvariant();
        BrokerRole = "queue";
    }

    internal List<Type> HandledMessageTypes { get; } = new();
    public int MessageCount => Agent?.As<ILocalQueue>().QueueCount ?? 0;

    /// <summary>
    /// GH-3856. A local queue is NEVER a single node listener regardless of its <see cref="ListenerScope"/>.
    /// It exists on every node, it never gets a <see cref="ListeningAgent"/> (BuildListenerAsync throws), and
    /// EndpointCollection.ExclusiveListeners() excludes it, so nothing ever starts the
    /// ListenerInboxRecoveryLoop that would otherwise own its inbox recovery. The per-database durability
    /// agent is the only recovery path a local queue has, and it is a perfectly good owner precisely because
    /// the queue lives on whichever node that agent happens to run on.
    /// </summary>
    internal override bool IsSingleNodeListener => false;

    public override bool ShouldEnforceBackPressure()
    {
        return false;
    }

    public override bool AutoStartSendingAgent()
    {
        return true;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException(
            $"{this} does not have a transport listener. A local queue is fed by in-process sends rather than by an external broker, so its ISendingAgent is both ends of it. See BuildAgent().");
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException(
            $"{this} does not have a transport sender. A local queue's ISendingAgent enqueues directly into its own execution block. See BuildAgent().");
    }

    protected internal override ISendingAgent StartSending(IWolverineRuntime runtime, Uri? replyUri)
    {
        Runtime = runtime;

        Compile(runtime);

        Agent = BuildAgent(runtime);

        return Agent;
    }

    internal ISendingAgent BuildAgent(IWolverineRuntime runtime)
    {
        return Mode switch
        {
            EndpointMode.BufferedInMemory => new BufferedLocalQueue(this, runtime),

            EndpointMode.Durable => new DurableLocalQueue(this, (WolverineRuntime)runtime),

            // GH-4022. Normally unreachable: ListenerConfiguration.ProcessInline() refuses a local queue eagerly, and
            // ListenerConfigurationValidator catches the lazily-configured queues at bootstrap. Kept as a
            // real message rather than a bare throw because this is the last line of defense for anything
            // that assigns Endpoint.Mode directly.
            EndpointMode.NativeAck => throw new NotSupportedException(
                $"{this} cannot run in {nameof(EndpointMode)}.{nameof(EndpointMode.NativeAck)}. Native acks settle a "
                + "delivery back to a message broker, and a local queue has no broker to settle against. Use "
                + $"{nameof(EndpointMode.BufferedInMemory)} or {nameof(EndpointMode.Durable)}. See GH-3708."),

            EndpointMode.Inline => throw new NotSupportedException(
                $"{this} cannot run in {nameof(EndpointMode)}.{nameof(EndpointMode.Inline)}. Inline means \"execute the message on the transport's own listening callback instead of queueing it\", and a local queue has no transport listener -- the queue itself is Wolverine's local execution block. Use {nameof(EndpointMode.BufferedInMemory)} or {nameof(EndpointMode.Durable)}."),

            _ => throw new InvalidOperationException($"Unknown {nameof(EndpointMode)} value '{Mode}' on {this}")
        };
    }

    public override string ToString()
    {
        return $"Local Queue '{EndpointName}'";
    }
}