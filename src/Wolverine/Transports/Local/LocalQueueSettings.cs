using System;
using System.Collections.Generic;
using Wolverine.Util;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

public class LocalQueueConfiguration : ListenerConfiguration<LocalQueueConfiguration, LocalQueueSettings>
{
    public LocalQueueConfiguration(LocalQueueSettings endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Add circuit breaker exception handling to this local queue. This will only
    /// be applied if the local queue is marked as durable!!!
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
    public LocalQueueSettings(string name) : base(EndpointRole.Application)
    {
        Name = name.ToLowerInvariant();
    }

    public LocalQueueSettings(Uri uri) : base(uri, EndpointRole.Application)
    {
    }

    public override bool ShouldEnforceBackPressure() => false;

    public override bool AutoStartSendingAgent() => true;

    public override Uri Uri => $"local://{Name}".ToUri();
    internal List<Type> HandledMessageTypes { get; } = new();
    

    public override void Parse(Uri uri)
    {
        Name = LocalTransport.QueueName(uri);
        Mode = EndpointMode.BufferedInMemory;
    }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
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
        return $"Local Queue '{Name}'";
    }
}
