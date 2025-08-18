using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine;

/// <summary>
///     Group of related, outgoing messages to use as a cascading message
///     mechanism in Wolverine message handlers
/// </summary>
public class OutgoingMessages : List<object>, IWolverineReturnType
{
    /// <summary>
    ///     Send a message back to the original sender
    /// </summary>
    /// <param name="response"></param>
    public void RespondToSender(object response)
    {
        Add(Respond.ToSender(response));
    }

    /// <summary>
    ///     Schedule a message to be sent (or executed) after a delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <typeparam name="T"></typeparam>
    public void Delay<T>(T message, TimeSpan delay)
    {
        Add(message, new DeliveryOptions { ScheduleDelay = delay });
    }

    /// <summary>
    ///     Schedule a message to be sent (or executed) at a specific time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <typeparam name="T"></typeparam>
    public void Schedule<T>(T message, DateTimeOffset time)
    {
        Add(message, new DeliveryOptions { ScheduledTime = time });
    }

    /// <summary>
    ///     Add an additional message with specific delivery options
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public void Add<T>(T message, DeliveryOptions options)
    {
        Add(new DeliveryMessage<T>(message, options));
    }

    /// <summary>
    /// Send a message to a specific endpoint by name, with optional delivery options
    /// </summary>
    /// <param name="message"></param>
    /// <param name="endpointName"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public void ToEndpoint<T>(T message, string endpointName, DeliveryOptions? options = null)
    {
        Add(new RoutedToEndpointMessage<T>(endpointName, message, options));
    }

    /// <summary>
    /// Send a message to a specific destination by Uri, with optional delivery options
    /// </summary>
    /// <param name="message"></param>
    /// <param name="destination"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public void ToDestination<T>(T message, Uri destination, DeliveryOptions? options = null)
    {
        Add(new RoutedToEndpointMessage<T>(destination, message, options));
    }
}

internal class OutgoingMessagesPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var outgoingVariables = chain.ReturnVariablesOfType<OutgoingMessages>();
            foreach (var outgoing in outgoingVariables)
            {
                outgoing.UseReturnAction(v => new CaptureCascadingMessages(v));
            }
        }
    }
}