using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Wolverine.Persistence;

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

public class DurabilitySettings : IDescribeMyself
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
    ///     Set by tenancy integrations (e.g. Wolverine's conjoined EF Core multi-tenancy)
    ///     that need the message store to provision its wolverine_tenants registry table
    ///     even without database-per-tenant master table tenancy
    /// </summary>
    public bool TenantRegistryRequired { get; set; }

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
    /// Opt-in reconciliation for when more than one registered message store claims the <c>Main</c> role
    /// (GH-3226) — e.g. an event-store-backed main store (Marten / Polecat <c>IntegrateWithWolverine()</c>)
    /// combined with a database-backed transport (the SQL Server / PostgreSQL queues) that also registers an
    /// implicit <c>Main</c> store. The callback receives every <c>Main</c>-tagged store and returns the one to
    /// keep as <c>Main</c>; the others are demoted to <c>Ancillary</c> instead of Wolverine throwing
    /// "There must be exactly one message store tagged as the 'main' store". When left null (the default) the
    /// strict single-Main validation is enforced. Return null from the callback to also fall back to the
    /// strict validation.
    /// </summary>
    public Func<IReadOnlyList<Wolverine.Persistence.Durability.IMessageStore>,
        Wolverine.Persistence.Durability.IMessageStore?>? ResolveMainStoreOnConflict { get; set; }

    /// <summary>
    /// Direct Wolverine on how it judges message identity. "Classic" default is IdOnly. Switch to IdAndDestination
    /// for Modular Monolith usage where you may be receiving the same message and processing separately in different
    /// external transport listening endpoints
    /// </summary>
    public MessageIdentity MessageIdentity { get; set; } = MessageIdentity.IdOnly;

    /// <summary>
    /// GH-4012. The maximum number of times Wolverine will try to settle (ack/nack) a single broker
    /// delivery before giving up and letting the broker redeliver it.
    ///
    /// This is a budget shared across the whole completion path via <c>Envelope.AckAttempts</c>,
    /// which is the point: <c>RetryBlock.MaximumAttempts</c> bounds retries within a single
    /// <c>PostAsync</c>, but the durable path stacks two of them
    /// (<c>DurableReceiver._completeBlock</c> -> <c>Listener.CompleteAsync</c> ->
    /// <c>RabbitMqChannelCallback.Complete</c>), so their budgets multiplied to nine broker round
    /// trips with neither block able to see the other's count.
    ///
    /// Note this cannot bound a redeliver -> dedupe -> re-ack loop: every redelivery constructs a
    /// brand new envelope, so the counter starts over. Only a broker-side delivery count can do
    /// that.
    /// </summary>
    public int MaximumAckAttempts { get; set; } = 3;

    /// <summary>
    /// GH-3711. How many successful handler completions a durable endpoint coalesces into one
    /// batched mark-as-handled <c>UPDATE</c> against the inbox. Completions accumulate for at most
    /// <see cref="Wolverine.Runtime.WorkerQueues.DurableReceiver.MarkAsHandledBatchWindow" /> (5ms)
    /// or until this many are pending, whichever comes first, and are then marked handled in a single
    /// round trip -- the completion-side twin of the batched inbox insert. A batch that fails falls
    /// back to marking each envelope individually with retries. Set to 1 to mark every completion in
    /// its own round trip as before. Default 100.
    /// </summary>
    public int MarkAsHandledBatchSize { get; set; } = 100;

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
    /// When true, scheduled-for-later messages destined for non-durable
    /// <see cref="Transports.Local.BufferedLocalQueue"/> instances route to
    /// <c>IMessageStore.Inbox</c> instead of the in-process
    /// <c>IScheduledJobProcessor</c>. Set via
    /// <see cref="IPolicies.AlwaysMakeScheduledMessagesDurable"/>.
    ///
    /// Other scheduling paths already provide durability without this flag — see the
    /// XML doc on <see cref="IPolicies.AlwaysMakeScheduledMessagesDurable"/> for the
    /// full matrix. No-ops when no message store is configured.
    /// </summary>
    public bool AlwaysMakeScheduledMessagesDurable { get; set; }

    /// <summary>
    ///     How long should successfully handled messages be kept to use in idempotency checking
    /// </summary>
    public TimeSpan KeepAfterMessageHandling { get; set; } = 5.Minutes();

    /// <summary>
    ///     Polling interval for the background cleanup of expired, successfully handled incoming
    ///     envelopes (the idempotency records). This cleanup runs on its own timer in a dedicated
    ///     transaction, separate from the main recovery loop, so a slow cleanup cannot block inbox
    ///     recovery work. Default is 1 minute.
    /// </summary>
    public TimeSpan HandledMessageCleanupPollingTime { get; set; } = 1.Minutes();

    /// <summary>
    ///     The maximum number of expired, handled incoming envelopes deleted in a single bounded
    ///     DELETE statement for providers that support batching (currently PostgreSQL and SQL Server).
    ///     Smaller batches hold locks for less time and reduce contention with live inbox traffic
    ///     under heavy load. Default is 5000.
    /// </summary>
    public int HandledMessageCleanupBatchSize { get; set; } = 5000;

    /// <summary>
    ///     Safety cap on how many delete batches the handled-envelope cleanup runs in a single
    ///     polling cycle before yielding. Any remaining expired rows are cleaned up on the next
    ///     cycle. Default is 20.
    /// </summary>
    public int HandledMessageCleanupMaxBatchesPerCycle { get; set; } = 20;

    /// <summary>
    ///     Governs the page size for how many persisted incoming or outgoing messages
    ///     will be loaded at one time for attempted retries or scheduled jobs
    /// </summary>
    public int RecoveryBatchSize { get; set; } = 100;

    /// <summary>
    ///     GH-3971: polling interval for the sweep that releases inbox/outbox messages owned by nodes
    ///     that no longer exist. This runs on its own timer in a dedicated transaction, separate from the
    ///     main recovery loop.
    ///
    ///     <para>It used to ride <see cref="ScheduledJobPollingTime" />, which also drives scheduled
    ///     message execution and the recoverable-message checks — so slowing the sweep on a large inbox
    ///     also delayed scheduled delivery, an unrelated trade the operator never asked for. Default is 5
    ///     seconds, matching the previous cadence.</para>
    /// </summary>
    public TimeSpan OrphanedMessageSweepPollingTime { get; set; } = 5.Seconds();

    /// <summary>
    ///     GH-3971: the maximum number of envelopes whose ownership is released in a single bounded
    ///     UPDATE, for providers that support batching (currently PostgreSQL and SQL Server).
    ///
    ///     <para>Losing one node makes every envelope it owned qualify at once, in one statement. A
    ///     reported deployment lost ~910,000 rows' worth across its shards on a single node loss, with a
    ///     12 KB average body — several GB of rewrite per sweep run per shard, all of it holding locks
    ///     against live inbox traffic. Default is 5000.</para>
    /// </summary>
    public int OrphanedMessageReleaseBatchSize { get; set; } = 5000;

    /// <summary>
    ///     GH-3971: safety cap on how many release batches the orphaned-message sweep runs in a single
    ///     polling cycle before yielding. Any remaining orphans are picked up on the next cycle.
    ///     Default is 20.
    /// </summary>
    public int OrphanedMessageReleaseMaxBatchesPerCycle { get; set; } = 20;

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
    ///     GH-3604 / D1: how many consecutive health-check ticks the observing node must see another node
    ///     as stale before it destructively deletes that node's row (which also releases the node's
    ///     in-flight envelope ownership and its agent assignments). Routing to a stale node stops
    ///     immediately regardless — it is dropped from the assignment grid on the first observation — so
    ///     this only adds hysteresis to the irreversible delete, preventing a single stale snapshot read or
    ///     transient blip from ejecting a node that is really alive. Minimum (and default) 2.
    /// </summary>
    public int StaleNodeEjectionThreshold { get; set; } = 2;

    /// <summary>
    ///     GH-3701: a hard cap on the number of rows retained in the node record table
    ///     (<c>wolverine_node_records</c>), the append-only diagnostic log written by
    ///     <c>INodeAgentPersistence.LogRecordsAsync</c> and read back by <c>FetchRecentRecordsAsync</c>.
    ///     <see cref="NodeEventRecordExpirationTime" /> bounds those rows by *age* only, which puts no ceiling on
    ///     the table at all: a cluster churning assignments writes one <c>AssignmentChanged</c> row per agent per
    ///     decision, so millions of rows a day fit comfortably inside the age window and turn an
    ///     agent-assignment incident into a database capacity problem on top of it. This cap is applied on the
    ///     same housekeeping pass as the age sweep, every <see cref="NodeRecordPruningPeriod" />, against the
    ///     <c>Main</c> store only. Raise it on very large agent universes, where one assignment wave is already
    ///     thousands of rows. Set to zero or a negative number to keep the age sweep as the only bound, which
    ///     was the behavior before 6.24.1.
    /// </summary>
    public int NodeRecordRetention { get; set; } = 10_000;

    /// <summary>
    ///     GH-3701: how often the node record table is pruned, both by age
    ///     (<see cref="NodeEventRecordExpirationTime" />) and down to <see cref="NodeRecordRetention" /> rows.
    ///     Deliberately far slower than <see cref="ScheduledJobPollingTime" /> — this is a housekeeping scan
    ///     over a table nothing on the hot path reads, and until 6.24.1 it was being appended to every
    ///     five-second recovery batch instead.
    /// </summary>
    public TimeSpan NodeRecordPruningPeriod { get; set; } = 1.Hours();

    /// <summary>
    ///     How often should Wolverine do a full check that all assigned agents are
    ///     really running and try to restart (or stop) any differences from the last
    ///     good set of assignments
    /// </summary>
    public TimeSpan CheckAssignmentPeriod { get; set; } = 30.Seconds();

    /// <summary>
    ///     GH-3604 / D3: the maximum number of agent assignments the leader packs into a single
    ///     <c>StartAgents</c> control message to a node. A node running a very large agent universe
    ///     (e.g. database-per-tenant Marten with thousands of subscription shards) cannot start
    ///     thousands of daemon agents inside one request/reply window, so assignments to a destination
    ///     are chunked into batches of this size and sent one chunk at a time. Default 50.
    /// </summary>
    public int AgentStartBatchSize { get; set; } = 50;

    /// <summary>
    ///     GH-3604 / D3: the maximum number of agents a receiving node starts concurrently when it
    ///     handles a <c>StartAgents</c> batch. Daemon-agent starts are I/O bound (database round-trips),
    ///     so starting them with bounded parallelism instead of serially lets a batch complete well
    ///     inside the reply window. Default 10.
    /// </summary>
    public int MaxAgentStartParallelism { get; set; } = 10;

    /// <summary>
    ///     GH-3604 / D3 (WO-7): the maximum number of agents this node stops concurrently when it is
    ///     draining every locally-running agent on shutdown. The old sequential drain
    ///     (<c>stopAllAgentsAsync</c>) could not finish thousands of daemon subscription agents inside a
    ///     typical 30s SIGTERM grace window, so agents were SIGKILLed mid-stop with unflushed progression.
    ///     A bounded fan-out makes the shutdown window usable at scale. Default 10.
    /// </summary>
    public int MaxAgentStopParallelism { get; set; } = 10;

    /// <summary>
    ///     GH-3748: once a batched agent command's initial reply window has elapsed without an answer,
    ///     the leader stops waiting passively and starts asking the destination node which of the
    ///     requested agents are actually running (or stopped) at this interval. Each poll is a cheap
    ///     read of the node's in-memory agent registry, so the interval mostly decides how quickly the
    ///     leader notices convergence. Default 10 seconds.
    /// </summary>
    public TimeSpan AgentProgressPollInterval { get; set; } = 10.Seconds();

    /// <summary>
    ///     GH-3748 / GH-3750: how long the leader tolerates ZERO observed progress on an in-flight
    ///     agent batch before giving up on the unconfirmed remainder and letting the next assignment
    ///     evaluation re-decide it. Any progress — one more agent confirmed running or stopped —
    ///     resets this clock, so a node that is slow but converging gets unbounded time while a node
    ///     that is wedged or gone costs a bounded wait. Sized to the slowest legitimate single agent
    ///     start we know of: a Marten projection shard replaying behind a version bump under the
    ///     daemon's bounded side-effect gate, which has a five-minute ceiling. Default 5 minutes.
    /// </summary>
    public TimeSpan AgentProgressStallTimeout { get; set; } = 5.Minutes();

    /// <summary>
    ///     GH-3519: how many extra times this node immediately re-tries an agent that failed to start,
    ///     before giving up and leaving it to the next assignment reevaluation. A first-assignment start
    ///     races the subsystems the agent depends on — an event-subscription shard evaluated before its
    ///     store's high-water detection is up is the reported case, and on a multi-store host a different
    ///     shard lost that race on every boot. Without a local retry the loser waited a full
    ///     <see cref="CheckAssignmentPeriod" /> (30s by default) doing nothing while its high-water mark
    ///     climbed. Set to 0 to restore the old single-attempt behavior. Default 2.
    /// </summary>
    public int AgentStartRetryAttempts { get; set; } = 2;

    /// <summary>
    ///     GH-3519: how long this node waits before each of the <see cref="AgentStartRetryAttempts" />
    ///     immediate re-tries of a failed agent start, multiplied by the attempt number so the second
    ///     retry waits twice as long as the first. Sized for a startup race that resolves in well under a
    ///     second, not for an outage — a failure that outlives these attempts is left to the next
    ///     assignment reevaluation rather than retried harder here. Default 250ms.
    /// </summary>
    public TimeSpan AgentStartRetryDelay { get; set; } = 250.Milliseconds();

    /// <summary>
    ///     GH-3888: how many node-local auto-restarts a stalled event-subscription agent may burn
    ///     without its sequence advancing before it gives up on this node. Once the budget is exhausted,
    ///     the node releases the agent's assignment — provided another live node advertises the
    ///     capability to run it — so the leader can place it on a healthy peer instead of retrying the
    ///     same conditions in the same place forever. Any observed progress resets the budget. Only a
    ///     self-healing failure (<c>ShardFailureCategory.Other</c>) ever reaches the auto-restart path
    ///     at all; per-event failures are surfaced and left alone (GH-3638). Set to 0 or a negative
    ///     number to disable release entirely and keep unbounded local retries (the pre-GH-3888
    ///     behavior). Default 3.
    /// </summary>
    public int MaxLocalAgentRestartsBeforeRelease { get; set; } = 3;

    /// <summary>
    ///     GH-3970: how many consecutive assignment ticks may fail to <i>build or start</i> an agent on this
    ///     node before the node releases it to a capable peer, using the same embargo as
    ///     <see cref="MaxLocalAgentRestartsBeforeRelease" />.
    ///
    ///     <para>This is the counterpart budget for a failure that happens <b>before</b> there is an agent
    ///     instance at all. A start that throws out of <c>IAgentFamily.BuildAgentAsync</c> leaves nothing
    ///     registered on the node, so the stall detector — which sweeps the agents this node is actually
    ///     running — never sees it, and no restart budget is ever consumed. Without this the leader learns
    ///     only that the agent is "unconfirmed", which it deliberately does not treat as a failure
    ///     (GH-3750), so the assignment stands and the same agent is requested on the same node again on
    ///     every tick, forever.</para>
    ///
    ///     <para>Note each tick has already made <see cref="AgentStartRetryAttempts" /> + 1 attempts of its
    ///     own before it counts as one failure here, so the default is a deliberately patient
    ///     three-strikes over three separate assignment cycles rather than a hair trigger. A successful
    ///     start clears the count. Set to 0 or a negative number to disable release for failed starts and
    ///     keep the pre-GH-3970 behavior of retrying on the same node indefinitely. Default 3.</para>
    /// </summary>
    public int MaxAgentStartFailuresBeforeRelease { get; set; } = 3;

    /// <summary>
    ///     GH-3888: how long a node withholds a released agent's URI from its advertised capabilities
    ///     after exhausting local restarts on it. While the embargo is live, the leader's
    ///     capability-matched distribution cannot hand the agent straight back to the node that just
    ///     failed it — the anti-bounce half of the release policy. After it lapses, the node advertises
    ///     the capability again and becomes an ordinary candidate; a node that is still sick will burn
    ///     another full restart budget before releasing again, so the worst case is one bounded move per
    ///     cooldown rather than a reassignment storm. A process restart clears all embargoes, since
    ///     capabilities are recaptured at startup. Default 10 minutes.
    /// </summary>
    public TimeSpan AgentReleaseCooldown { get; set; } = 10.Minutes();

    /// <summary>
    /// Opt-in switch for the dynamic listener registry: persisted listener URIs that
    /// are activated at runtime in addition to the listeners declared statically
    /// through <see cref="WolverineOptions"/>. When <c>true</c>, <c>IMessageStore.Listeners</c>
    /// is backed by durable storage (and database-backed message stores create their
    /// listener registry table on first migration); when <c>false</c> (the default),
    /// <c>IMessageStore.Listeners</c> is a no-op store and no listener-registry
    /// schema is provisioned. Default is <c>false</c> so users upgrading Wolverine
    /// see no schema migration churn.
    /// </summary>
    public bool EnableDynamicListeners { get; set; } = false;

    /// <summary>
    /// If using any kind of dynamic multi-tenancy where Wolverine should discover new
    /// tenants, this is the polling time. Default is 5 seconds
    /// </summary>
    public TimeSpan TenantCheckPeriod { get; set; } = 5.Seconds();

    private TimeSpan _updateMetricsPeriod = 5.Seconds();

    /// <summary>
    /// If using any kind of message persistence, this is the polling time
    /// to update the metrics on the persisted envelope counts. Default is 5 seconds.
    /// Must be greater than zero. Use <see cref="DurabilityMetricsEnabled"/> to turn
    /// the polling off entirely.
    /// </summary>
    public TimeSpan UpdateMetricsPeriod
    {
        get => _updateMetricsPeriod;
        set
        {
            // The metrics sweeper paces its pass with Task.Delay, so a non-positive period
            // would hot-spin the sweep loop against every registered database rather than
            // fail. Reject it at configuration time instead.
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(UpdateMetricsPeriod), value,
                    $"{nameof(UpdateMetricsPeriod)} must be greater than zero. Set {nameof(DurabilityMetricsEnabled)} to false to disable durability metrics polling.");
            }

            _updateMetricsPeriod = value;
        }
    }

    /// <summary>
    /// Is the polling for durability metrics enabled? Default is true
    /// </summary>
    public bool DurabilityMetricsEnabled { get; set; } = true;

    /// <summary>
    /// Declares how many connections this application may take against each database server, and
    /// surfaces how many are actually in use. Rides the same sweep as the durability metrics, so
    /// it is silent unless <see cref="DurabilityMetricsEnabled"/> is true. See #3397.
    /// </summary>
    /// <example>
    /// <code>
    /// opts.Durability.ConnectionBudgets
    ///     .ForServer("pg-shard-a", 5432, maxConnections: 400)
    ///     .ForServer("pg-shard-b", 5432, maxConnections: 200);
    /// </code>
    /// </example>
    public ConnectionBudgets ConnectionBudgets { get; } = new();

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
    /// Health-check threshold for dead-letter-queue growth. When the persisted DLQ count grows
    /// faster than this many envelopes per minute between two consecutive health-check evaluations,
    /// the durability agent reports Degraded. Default is 100/min. See #2646.
    /// </summary>
    public int HealthDeadLetterGrowthPerMinuteThreshold { get; set; } = 100;

    /// <summary>
    /// Health-check threshold for stuck recovery / scheduled-job pollers. When the persisted
    /// inbox+outbox count (or the scheduled count) is non-zero and has not decreased over this
    /// many consecutive evaluations, the durability agent reports Degraded. Default is 3. See #2646.
    /// </summary>
    public int HealthStuckPollCycleThreshold { get; set; } = 3;

    /// <summary>
    /// Health-check threshold for consecutive persistence-layer failures. After this many
    /// consecutive failed poll cycles, the durability agent reports Unhealthy (a single failure
    /// reports Degraded). Default is 3. See #2646.
    /// </summary>
    public int HealthConsecutiveFailureUnhealthyThreshold { get; set; } = 3;

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
    /// Maximum time to wait for in-flight message handlers to complete during graceful
    /// shutdown before proceeding with the shutdown sequence. Default is 30 seconds.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = 30.Seconds();

    /// <summary>
    ///     Get or set the logical Wolverine service name. By default, this is
    ///     derived from the name of a custom WolverineOptions
    /// </summary>
    internal void Cancel()
    {
        _cancellation.Cancel();
    }

    public OptionsDescription ToDescription()
    {
        var desc = new OptionsDescription { Subject = "Wolverine.DurabilitySettings" };
        desc.AddValue(nameof(Mode), Mode);
        desc.AddValue(nameof(MessageIdentity), MessageIdentity);
        desc.AddValue(nameof(DurabilityAgentEnabled), DurabilityAgentEnabled);
        desc.AddValue(nameof(RecoveryBatchSize), RecoveryBatchSize);
        desc.AddValue(nameof(OrphanedMessageSweepPollingTime), OrphanedMessageSweepPollingTime);
        desc.AddValue(nameof(OrphanedMessageReleaseBatchSize), OrphanedMessageReleaseBatchSize);
        desc.AddValue(nameof(OrphanedMessageReleaseMaxBatchesPerCycle), OrphanedMessageReleaseMaxBatchesPerCycle);
        desc.AddValue(nameof(KeepAfterMessageHandling), KeepAfterMessageHandling);
        desc.AddValue(nameof(NodeReassignmentPollingTime), NodeReassignmentPollingTime);
        desc.AddValue(nameof(MetricsCollectionSamplingInterval), MetricsCollectionSamplingInterval);
        desc.AddValue(nameof(ScheduledJobPollingTime), ScheduledJobPollingTime);
        desc.AddValue(nameof(HealthCheckPollingTime), HealthCheckPollingTime);
        desc.AddValue(nameof(StaleNodeTimeout), StaleNodeTimeout);
        desc.AddValue(nameof(CheckAssignmentPeriod), CheckAssignmentPeriod);
        desc.AddValue(nameof(TenantCheckPeriod), TenantCheckPeriod);
        desc.AddValue(nameof(UpdateMetricsPeriod), UpdateMetricsPeriod);
        desc.AddValue(nameof(DurabilityMetricsEnabled), DurabilityMetricsEnabled);
        desc.AddValue(nameof(DeadLetterQueueExpirationEnabled), DeadLetterQueueExpirationEnabled);
        desc.AddValue(nameof(DeadLetterQueueExpiration), DeadLetterQueueExpiration);
        desc.AddValue(nameof(NodeEventRecordExpirationTime), NodeEventRecordExpirationTime);
        desc.AddValue(nameof(NodeRecordRetention), NodeRecordRetention);
        desc.AddValue(nameof(NodeRecordPruningPeriod), NodeRecordPruningPeriod);
        desc.AddValue(nameof(MaxLocalAgentRestartsBeforeRelease), MaxLocalAgentRestartsBeforeRelease);
        desc.AddValue(nameof(MaxAgentStartFailuresBeforeRelease), MaxAgentStartFailuresBeforeRelease);
        desc.AddValue(nameof(AgentReleaseCooldown), AgentReleaseCooldown);
        desc.AddValue(nameof(SendingAgentIdleTimeout), SendingAgentIdleTimeout);
        desc.AddValue(nameof(DrainTimeout), DrainTimeout);
        desc.AddValue(nameof(EnableInboxPartitioning), EnableInboxPartitioning);
        if (OutboxStaleTime.HasValue) desc.AddValue(nameof(OutboxStaleTime), OutboxStaleTime.Value);
        if (InboxStaleTime.HasValue) desc.AddValue(nameof(InboxStaleTime), InboxStaleTime.Value);
        if (MessageStorageSchemaName != null) desc.AddValue(nameof(MessageStorageSchemaName), MessageStorageSchemaName);
        return desc;
    }
}