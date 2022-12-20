using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Transports.Local;

public class LocalQueue : Endpoint
{
    public LocalQueue(string name) : base($"local://{name}".ToUri(), EndpointRole.Application)
    {
        EndpointName = name.ToLowerInvariant();
    }

    internal List<Type> HandledMessageTypes { get; } = new();

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