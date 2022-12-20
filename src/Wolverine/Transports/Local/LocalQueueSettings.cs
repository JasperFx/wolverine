using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.Transports.Local;

public class LocalQueueConfiguration : ListenerConfiguration<LocalQueueConfiguration, LocalQueueSettings>
{
    public LocalQueueConfiguration(LocalQueueSettings endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Limit all outgoing messages to a certain "deliver within" time span after which the messages
    /// will be discarded even if not successfully delivered or processed
    /// </summary>
    /// <param name="timeToLive"></param>
    /// <returns></returns>
    public LocalQueueConfiguration DeliverWithin(TimeSpan timeToLive)
    {
        add(e => e.OutgoingRules.Add(new DeliverWithinRule(timeToLive)));
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this local queue. This will only
    ///     be applied if the local queue is marked as durable!!!
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public LocalQueueConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }
}

public class LocalQueueSettings : Endpoint
{
    public LocalQueueSettings(string name) : base($"local://{name}".ToUri(), EndpointRole.Application)
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