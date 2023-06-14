using JasperFx.Core;

namespace Wolverine;

// TODO -- thin this down after eliminating the old DurabilityAgent
public class DurabilitySettings
{
    private readonly CancellationTokenSource _cancellation = new();

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
    ///     Default is false. Turn this on to see when every polling DurabilityAgent
    ///     action executes. Warning, it's a LOT of noise
    /// </summary>
    public bool VerboseDurabilityAgentLogging { get; set; } = false;


    /// <summary>
    ///     Governs the page size for how many persisted incoming or outgoing messages
    ///     will be loaded at one time for attempted retries or scheduled jobs
    /// </summary>
    public int RecoveryBatchSize { get; set; } = 100;

    /// <summary>
    ///     How frequently Wolverine will attempt to reassign incoming or outgoing
    ///     persisted methods from nodes that are detected to be offline
    /// </summary>
    public TimeSpan NodeReassignmentPollingTime { get; set; } = 1.Minutes();

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
    public TimeSpan ScheduledJobFirstExecution { get; set; } = 0.Seconds();

    /// <summary>
    ///     Polling interval for executing scheduled messages
    /// </summary>
    public TimeSpan ScheduledJobPollingTime { get; set; } = 5.Seconds();

    public int AssignedNodeNumber { get; internal set; } = Guid.NewGuid().ToString().GetDeterministicHashCode();

    public CancellationToken Cancellation => _cancellation.Token;

    // TODO -- add Xml API comments
    public TimeSpan FirstHealthCheckExecution { get; set; } = 3.Seconds();
    public TimeSpan HealthCheckPollingTime { get; set; } = 10.Seconds();

    public TimeSpan StaleNodeTimeout { get; set; } = 1.Minutes();

    /// <summary>
    ///     How long should Wolverine buffer requests to evaluate assignments
    ///     to prevent unnecessary assignment operations
    /// </summary>
    public TimeSpan EvaluateAssignmentBufferTime { get; set; } = 1.Seconds();

    /// <summary>
    ///     How often should Wolverine do a full check that all assigned agents are
    ///     really running and try to restart (or stop) any differences from the last
    ///     good set of assignments
    /// </summary>
    public TimeSpan CheckAssignmentPeriod { get; set; } = 30.Seconds();


    /// <summary>
    ///     Get or set the logical Wolverine service name. By default, this is
    ///     derived from the name of a custom WolverineOptions
    /// </summary>
    internal void Cancel()
    {
        _cancellation.Cancel();
    }
}