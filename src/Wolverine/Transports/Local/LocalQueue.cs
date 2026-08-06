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
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
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

            EndpointMode.Inline => throw new NotSupportedException(),
            _ => throw new InvalidOperationException()
        };
    }

    public override string ToString()
    {
        return $"Local Queue '{EndpointName}'";
    }
}