using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.Postgresql.Transport.NServiceBus;

public class NServiceBusPostgresqlListenerConfiguration
    : ListenerConfiguration<NServiceBusPostgresqlListenerConfiguration, NServiceBusPostgresqlQueue>
{
    public NServiceBusPostgresqlListenerConfiguration(NServiceBusPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Receive each message inside a database transaction that stays open while its handler runs, the way
    ///     NServiceBus's own SQL transport receives. The message's row is deleted only when the handler succeeds;
    ///     a message that is requeued, or whose process dies mid-handler, is put back exactly where it was in the
    ///     queue. Without this, an Inline listener deletes the row as it receives the message.
    ///     <para>
    ///     Requires <c>ProcessInline()</c>, and the host refuses to start otherwise. Messages are received one at
    ///     a time, and each holds a database connection and an open transaction for as long as its handler runs.
    ///     </para>
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration UseReceiveTransaction()
    {
        add(e => e.UseReceiveTransaction = true);
        return this;
    }

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that foreign endpoints should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener.
    /// </summary>
    public NServiceBusPostgresqlListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });
        return this;
    }
}

public class NServiceBusPostgresqlSubscriberConfiguration
    : SubscriberConfiguration<NServiceBusPostgresqlSubscriberConfiguration, NServiceBusPostgresqlQueue>
{
    public NServiceBusPostgresqlSubscriberConfiguration(NServiceBusPostgresqlQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that the receiving endpoint should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusPostgresqlSubscriberConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }
}
