using System;
using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine;

public interface IPolicies
{
    /// <summary>
    /// Add a new endpoint policy
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void Add<T>() where T : IEndpointPolicy, new();

    /// <summary>
    /// Add a new endpoint policy
    /// </summary>
    /// <param name="policy"></param>
    void Add(IEndpointPolicy policy);

    /// <summary>
    /// Set all non local listening endpoints to be enrolled into durable inbox 
    /// </summary>
    void UseDurableInboxOnAllListeners();   
    
    /// <summary>
    /// Set all local queues to be enrolled into durability
    /// </summary>
    void UseDurableLocalQueues();
    
    /// <summary>
    /// Set all outgoing, external endpoints to be enrolled into durable outbox sending
    /// </summary>
    void UseDurableOutboxOnAllSendingEndpoints();

    /// <summary>
    /// Create a policy for all listening *non local* endpoints
    /// </summary>
    /// <param name="configure"></param>
    void AllListeners(Action<ListenerConfiguration> configure);

    /// <summary>
    /// Create a policy for all *non local* subscriber endpoints
    /// </summary>
    /// <param name="configure"></param>
    void AllSenders(Action<ISubscriberConfiguration> configure);

    /// <summary>
    /// Create a policy for all local queues
    /// </summary>
    /// <param name="configure"></param>
    void AllLocalQueues(Action<IListenerConfiguration> configure);

}