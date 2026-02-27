using JasperFx.Core;
using JasperFx.MultiTenancy;

namespace Wolverine;

public enum DurabilityMode
{
    /// <summary>
    /// The durability agent will be optimized to run in a single node. This is very useful
    /// for local development where you may be frequently stopping and restarting the service
    ///
    /// All known agents will automatically start on the local node. The recovered inbox/outbox
    /// messages will start functioning immediately
    /// </summary>
    Solo,

    /// <summary>
    /// Normal mode that assumes that Wolverine is running on multiple load balanced nodes
    /// with messaging active
    /// </summary>
    Balanced,

    /// <summary>
    /// Disables all message persistence to optimize Wolverine for usage within serverless functions
    /// like AWS Lambda or Azure Functions. Requires that all endpoints be inline
    /// </summary>
    Serverless,

    /// <summary>
    /// Optimizes Wolverine for usage as strictly a mediator tool. This completely disables all node
    /// persistence including the inbox and outbox
    /// </summary>
    MediatorOnly
}

/// <summary>
/// Controls how Wolverine chooses to identify received message uniqueness in message storage
/// </summary>
public enum MessageIdentity
{
    /// <summary>
    /// The default, "classic" behavior where Wolverine only identifies a received message by the unique message id
    /// </summary>
    IdOnly,
    
    /// <summary>
    /// Make Wolverine identify message identity uniqueness by a combination of the message id and destination (received_at). Use
    /// this if you are having a single Wolverine process receive the same message from multiple external listeners. This may be
    /// necessary for some "Modular Monolith" approaches
    /// </summary>
    IdAndDestination
}

public class DurabilitySettings
{
    private readonly CancellationTokenSource _cancellation = new();
    private TenantIdStyle _tenantIdStyle = TenantIdStyle.CaseSensitive;

    /// <summary>
    /// For systems that use multi-tenancy, this controls how Wolverine does or does not "correct" the supplied tenant
    /// id, with the default behavior being to use case-sensitive tenant ids.
    ///
    /// Use the IServiceCollection.CritterStackDefaults() method to change this 
    /// </summary>
    public TenantIdStyle TenantIdStyle
    {
        get => _tenantIdStyle;
        internal set
        {
            _tenantIdStyle = value;
            TenantIdStyleHasChanged = true;
        }
    }
    
    internal bool TenantIdStyleHasChanged { get; set; }

    /// <summary>
    /// If set, this establishes a default database schema name for all registered message
    /// storage databases. Use this with a modular monolith approach where all modules target the same physical database. The default is null.
    /// </summary>
    public string? MessageStorageSchemaName { get; set; } = null;
    
    /// <summary>
    /// Control and optimize the durability agent behavior within Wolverine applications
    /// </summary>
    public DurabilityMode Mode { get; set; } = DurabilityMode.Balanced;

    /// <summary>
    /// Direct Wolverine on how it judges message identity. "Classic" default is IdOnly. Switch to IdAndDestination
    /// for Modular Monolith usage where you may be receiving the same message and processing separately in different
    /// external transport listening endpoints
    /// </summary>
    public MessageIdentity MessageIdentity { get; set; } = MessageIdentity.IdOnly;

    /// <summary>
    /// If non-null, this directs Wolverine to "push" any message in the durable outbox that is older
    /// than the configured time even if the message is marked as owned by an active node
    /// </summary>
    public TimeSpan? OutboxStaleTime { get; set; }
    
    /// <summary>
    /// If non-null, this directs Wolverine to "push" any message in the durable inbox that is older
    /// than the configured time even if the message is marked as owned by an active node. Should NOT ever
    /// be necessary, but it's an imperfect world. Enable this if you see "stuck" envelopes
    /// </summary>
    public TimeSpan? InboxStaleTime { get; set; }
    
    /// <summary>
    /// For persistence mechanisms that support this (PostgreSQL), this directs Wolverine to use partitioning
    /// based on the envelope status for the transactional inbox storage. This can be a performance optimization,
    /// but does require a database migration if enabled
    /// </summary>
    public bool EnableInboxPartitioning { get; set; }

    /// <summary>
    ///     Should the message durability agent be enabled during execution.
    ///     The default is true.
    /// </summary>
    public bool DurabilityAgentEnabled { get; set; } = true;

    /// <summary>
    ///     How long should successfully handled messages be kept to use in idempotency checking
    /// </summary>
    public TimeSpan KeepAfterMessageHandling { get; set; } = 5.Minutes();

    /// <summary>
    ///     Governs the page size for how many persisted incoming or outgoing messages
    ///     will be loaded at one time for attempted retries or scheduled jobs
    /// </summary>
    public int RecoveryBatchSize { get; set; } = 100;

    /// <summary>
    ///     How frequently Wolverine will attempt to reassign incoming or outgoing
    ///     persisted methods from nodes that are detected to be offline
    /// </summary>
    public TimeSpan NodeReassignmentPollingTime { get; set; } = 5.Seconds();

    /// <summary>
    ///     When should the first execution of the node reassignment job
    ///     execute after application startup.
    /// </summary>
    public TimeSpan FirstNodeReassignmentExecution { get; set; } = 0.Seconds();

    /// <summary>
    ///     Interval between collecting persisted and queued message metrics
    /// </summary>
    public TimeSpan MetricsCollectionSamplingInterval { get; set; } = 5.Seconds();

    /// <summary>
    ///     How long to wait before the first execution of polling
    ///     for ready, persisted scheduled messages
    /// </summary>
    public TimeSpan ScheduledJobFirstExecution { get; set; } = Random.Shared.Next(500, 5000).Milliseconds();

    /// <summary>
    ///     Polling interval for executing scheduled messages
    /// </summary>
    public TimeSpan ScheduledJobPollingTime { get; set; } = 5.Seconds();

    public int AssignedNodeNumber { get; internal set; } = Guid.NewGuid().ToString().GetDeterministicHashCode();

    public CancellationToken Cancellation => _cancellation.Token;


    /// <summary>
    /// Time span before the first health check is executed
    /// </summary>
    public TimeSpan FirstHealthCheckExecution { get; set; } = 3.Seconds();

    /// <summary>
    /// Polling time between health checks
    /// </summary>
    public TimeSpan HealthCheckPollingTime { get; set; } = 10.Seconds();

    /// <summary>
    /// Age of health check data before a node is considered to be "stale" or dormant and
    /// will be recovered by the durability agent
    /// </summary>
    public TimeSpan StaleNodeTimeout { get; set; } = 1.Minutes();

    /// <summary>
    ///     How often should Wolverine do a full check that all assigned agents are
    ///     really running and try to restart (or stop) any differences from the last
    ///     good set of assignments
    /// </summary>
    public TimeSpan CheckAssignmentPeriod { get; set; } = 30.Seconds();

    /// <summary>
    /// If using any kind of dynamic multi-tenancy where Wolverine should discover new
    /// tenants, this is the polling time. Default is 5 seconds
    /// </summary>
    public TimeSpan TenantCheckPeriod { get; set; } = 5.Seconds();

    /// <summary>
    /// If using any kind of message persistence, this is the polling time
    /// to update the metrics on the persisted envelope counts. Default is 5 seconds
    /// </summary>
    public TimeSpan UpdateMetricsPeriod { get; set; } = 5.Seconds();

    /// <summary>
    /// Is the polling for durability metrics enabled? Default is true
    /// </summary>
    public bool DurabilityMetricsEnabled { get; set; } = true;

    /// <summary>
    /// If DeadLetterQueueExpirationEnabled is true, this governs how long persisted
    /// dead letter queue messages will be retained. The default is 10 days from the time
    /// the message is persisted.
    /// </summary>
    public TimeSpan DeadLetterQueueExpiration { get; set; } = 10.Days();
    
    /// <summary>
    /// Opt-in flag governs whether or not dead letter queued messages have expiration
    /// enforced for database stored dead letter messages. The default is false.
    /// </summary>
    public bool DeadLetterQueueExpirationEnabled { get; set; }

    /// <summary>
    /// Configurable time to keep records in the wolverine_node_records storage (or equivalent) for node records.
    /// Default is 5 days
    /// </summary>
    public TimeSpan NodeEventRecordExpirationTime { get; set; } = 5.Days();

    /// <summary>
    ///     How long a sending agent can be idle before it is considered stale
    ///     and eligible for cleanup. Default is 5 minutes.
    /// </summary>
    public TimeSpan SendingAgentIdleTimeout { get; set; } = 5.Minutes();

    /// <summary>
    /// When this option is enabled retry block used in InlineSendingAgent will synchronously wait on sending task to assure the message is send.
    /// When set to <see langword="false"/> default behavior is used so InlineSendingAgent agent will try to send a message and when failed it will give control to caller and retry on other thread in async manner
    /// </summary>
    public bool UseSyncRetryBlock { get; set; }

    /// <summary>
    /// Controls whether health check operations emit telemetry traces for 'wolverine_node_assignments'. 
    /// Default is true to maintain backwards compatibility. Set to false to completely disable health check tracing.
    /// </summary>
    public bool NodeAssignmentHealthCheckTracingEnabled { get; set; } = true;

    /// <summary>
    /// When set, health check traces will be throttled to emit at most once per this time period.
    /// For example, set to 10 minutes to only emit traces every 10 minutes instead of every health check.
    /// This reduces telemetry volume while still providing periodic visibility.
    /// Default is null (no throttling - all health checks are traced when NodeAssignmentHealthCheckTracingEnabled is true).
    /// </summary>
    public TimeSpan? NodeAssignmentHealthCheckTraceSamplingPeriod { get; set; }

    /// <summary>
    ///     Get or set the logical Wolverine service name. By default, this is
    ///     derived from the name of a custom WolverineOptions
    /// </summary>
    internal void Cancel()
    {
        _cancellation.Cancel();
    }
}