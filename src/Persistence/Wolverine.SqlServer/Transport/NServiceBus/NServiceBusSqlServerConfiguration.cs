using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.SqlServer.Transport.NServiceBus;

public class NServiceBusSqlServerListenerConfiguration
    : ListenerConfiguration<NServiceBusSqlServerListenerConfiguration, NServiceBusSqlServerQueue>
{
    public NServiceBusSqlServerListenerConfiguration(NServiceBusSqlServerQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The maximum number of messages to receive in a single poll. Default is 20.
    /// </summary>
    public NServiceBusSqlServerListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    ///     Configure how often to poll for new messages when the queue is idle.
    /// </summary>
    public NServiceBusSqlServerListenerConfiguration PollingInterval(TimeSpan interval)
    {
        add(e => e.PollingInterval = interval);
        return this;
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that foreign endpoints should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusSqlServerListenerConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }

    /// <summary>
    ///     Map an NServiceBus tenant header on incoming messages to Wolverine's
    ///     <c>Envelope.TenantId</c> (and stamp it on replies). NServiceBus multi-tenancy carries
    ///     the tenant id in a user-defined header that the receiving endpoint's
    ///     <c>MultiTenantConnectionBuilder</c> resolves to a tenant database; the Particular
    ///     SQL-persistence sample uses <c>tenant_id</c>. Pass the header name your NServiceBus
    ///     endpoint is configured with.
    /// </summary>
    public NServiceBusSqlServerListenerConfiguration MapTenantIdFromHeader(string headerName = "tenant_id")
    {
        add(e => e.TenantHeader = headerName);
        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener.
    /// </summary>
    public NServiceBusSqlServerListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });
        return this;
    }
}

public class NServiceBusSqlServerSubscriberConfiguration
    : SubscriberConfiguration<NServiceBusSqlServerSubscriberConfiguration, NServiceBusSqlServerQueue>
{
    public NServiceBusSqlServerSubscriberConfiguration(NServiceBusSqlServerQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     The bare NServiceBus queue/table name that the receiving endpoint should reply to.
    ///     When not set, the Wolverine endpoint marked <c>UseForReplies()</c> is used.
    /// </summary>
    public NServiceBusSqlServerSubscriberConfiguration ReplyQueueName(string queueName)
    {
        add(e => e.InteropReplyQueueName = queueName);
        return this;
    }

    /// <summary>
    ///     Stamp Wolverine's <c>Envelope.TenantId</c> onto an NServiceBus tenant header for
    ///     messages published to this NServiceBus endpoint, so a multi-tenant NServiceBus endpoint
    ///     resolves the correct tenant database via its <c>MultiTenantConnectionBuilder</c>. The
    ///     Particular SQL-persistence sample uses <c>tenant_id</c>; pass the header name your
    ///     NServiceBus endpoint expects.
    /// </summary>
    public NServiceBusSqlServerSubscriberConfiguration MapTenantIdToHeader(string headerName = "tenant_id")
    {
        add(e => e.TenantHeader = headerName);
        return this;
    }
}
