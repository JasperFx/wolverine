using Wolverine.Configuration;
using Wolverine.Nats.Internal;

namespace Wolverine.Nats.Configuration;

public class NatsListenerConfiguration
    : ListenerConfiguration<NatsListenerConfiguration, NatsEndpoint>
{
    public NatsListenerConfiguration(NatsEndpoint endpoint)
        : base(endpoint) { }

    /// <summary>
    /// Use JetStream for durable messaging
    /// </summary>
    public NatsListenerConfiguration UseJetStream(
        string? streamName = null,
        string? consumerName = null
    )
    {
        add(endpoint =>
        {
            endpoint.UseJetStream = true;
            endpoint.StreamName = streamName ?? endpoint.Subject.Replace(".", "_").ToUpper();
            endpoint.ConsumerName = consumerName;
        });

        return this;
    }

    /// <summary>
    /// Use a queue group for load balancing (Core NATS only)
    /// </summary>
    public NatsListenerConfiguration UseQueueGroup(string queueGroup)
    {
        add(endpoint =>
        {
            endpoint.QueueGroup = queueGroup;
        });

        return this;
    }

    /// <summary>
    /// Configure dead letter queue settings for this NATS listener
    /// </summary>
    public NatsListenerConfiguration ConfigureDeadLetterQueue(
        int maxDeliveryAttempts,
        string? deadLetterSubject = null
    )
    {
        add(endpoint =>
        {
            endpoint.DeadLetterQueueEnabled = true;
            endpoint.DeadLetterSubject = deadLetterSubject;
            endpoint.MaxDeliveryAttempts = maxDeliveryAttempts;
        });

        return this;
    }

    /// <summary>
    /// Disable dead letter queue handling for this listener
    /// </summary>
    public NatsListenerConfiguration DisableDeadLetterQueueing()
    {
        add(endpoint =>
        {
            endpoint.DeadLetterQueueEnabled = false;
        });

        return this;
    }

    /// <summary>
    /// Configure the dead letter subject for failed messages
    /// </summary>
    public NatsListenerConfiguration DeadLetterTo(string deadLetterSubject)
    {
        add(endpoint =>
        {
            endpoint.DeadLetterSubject = deadLetterSubject;
        });

        return this;
    }
}
