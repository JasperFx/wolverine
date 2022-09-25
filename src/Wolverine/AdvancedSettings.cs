using System;
using System.Reflection;
using System.Threading;
using Baseline.Dates;
using Wolverine.Util;
using LamarCodeGeneration;
using LamarCodeGeneration.Model;

namespace Wolverine;

public class AdvancedSettings
{
    private readonly CancellationTokenSource _cancellation = new();
    private string _serviceName;


    public AdvancedSettings(Assembly? applicationAssembly)
    {
        _serviceName = applicationAssembly?.GetName().Name ?? "WolverineApplication";
        CodeGeneration = new GenerationRules("Internal.Generated");
        CodeGeneration.Sources.Add(new NowTimeVariableSource());

        CodeGeneration.Assemblies.Add(GetType().GetTypeInfo().Assembly);
        CodeGeneration.Assemblies.Add(applicationAssembly);
    }

    /// <summary>
    ///     Configure or extend the Lamar code generation
    /// </summary>
    public GenerationRules CodeGeneration { get; }

    /// <summary>
    ///     Should the message durability agent be enabled during execution.
    ///     The default is true.
    /// </summary>
    public bool DurabilityAgentEnabled { get; set; } = true;

    
    /// <summary>
    /// How long should successfully handled messages be kept to use in idempotency checking
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

    public int UniqueNodeId { get; } = Guid.NewGuid().ToString().GetDeterministicHashCode();

    public CancellationToken Cancellation => _cancellation.Token;

    /// <summary>
    ///     Get or set the logical Wolverine service name. By default, this is
    ///     derived from the name of a custom WolverineOptions
    /// </summary>

    public string? ServiceName
    {
        get => _serviceName;
        set
        {
            _serviceName = value ?? throw new InvalidOperationException("A non-null value is required");
        }
    }


    /// <summary>
    ///     This should probably *only* be used in development or testing
    ///     to latch all outgoing message sending
    /// </summary>
    public bool StubAllOutgoingExternalSenders { get; set; }

    internal void Cancel()
    {
        _cancellation.Cancel();
    }
}
