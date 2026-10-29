using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5016. One unit of work inside a single, externally triggered durability pass. See
///     <see cref="DurabilityPassExtensions.RunDurabilityPassAsync(Microsoft.Extensions.Hosting.IHost, DurabilityPassOptions?, CancellationToken)" />.
/// </summary>
/// <remarks>
///     <para>A message store contributes its own steps (outgoing recovery, scheduled message release, incoming
///     recovery, expiry cleanups) through <see cref="IMessageStoreWithDurabilityPass" />. Anything else, such as
///     catching up asynchronous projections, can be added by registering an <see cref="IDurabilityPassStep" /> in
///     the application's IoC container. Those run once per pass, under the main message store's pass lock.</para>
///     <para>A step should do at most one batch of <see cref="DurabilityPassContext.BatchSize" /> items per call
///     and report what it knows is left in <see cref="DurabilityPassStepResult.Remaining" />. The pass calls it
///     again while it reports remaining work, made progress, and the pass still has time left.</para>
/// </remarks>
public interface IDurabilityPassStep
{
    /// <summary>
    ///     Descriptive name of this step for the pass report and logging
    /// </summary>
    string Name { get; }

    Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token);
}

/// <summary>
///     GH-5016. Implemented by a message store that can take part in an externally triggered durability pass
/// </summary>
public interface IMessageStoreWithDurabilityPass : IMessageStore
{
    /// <summary>
    ///     Try to take this store's durability pass lock <b>without waiting</b>, an advisory lock or its
    ///     equivalent, so that two overlapping passes against the same database skip instead of sending the
    ///     same messages twice. Answer null when another pass already holds it. Disposing the returned value
    ///     releases the lock.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token);

    /// <summary>
    ///     Whether this store honors <see cref="DurabilitySettings.UseTransactionScopedDurabilityPassLock" />.
    ///     A store that does not keeps taking its usual lock, and the pass warns that the setting is ignored
    /// </summary>
    bool SupportsTransactionScopedDurabilityPassLock => false;

    /// <summary>
    ///     The steps this store runs in every durability pass, in order
    /// </summary>
    IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime);
}

/// <summary>
///     GH-5016. The bounds of one durability pass
/// </summary>
public class DurabilityPassOptions
{
    /// <summary>
    ///     The pass stops starting new work once this much time has gone by and reports what it did not
    ///     get to. A step that is already running is allowed to finish its current batch. Default is 30 seconds.
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The most items a step should handle per batch. Defaults to
    ///     <see cref="DurabilitySettings.RecoveryBatchSize" /> when not set.
    /// </summary>
    public int? BatchSize { get; set; }
}

/// <summary>
///     GH-5016. What one durability pass step sees
/// </summary>
public class DurabilityPassContext
{
    /// <summary>
    ///     The most row ids a step excludes from its queries in one pass because they already failed in it. A
    ///     step stops for the rest of the pass once it has more failures than this, so the id list stays well
    ///     inside Oracle's 1,000 expressions in an IN list and SQL Server's 2,100 parameters. See GH-5035
    /// </summary>
    public const int MaxExcludedIds = 500;

    private readonly Func<DateTimeOffset> _clock;

    internal DurabilityPassContext(IWolverineRuntime runtime, IMessageStore store, int batchSize,
        DateTimeOffset deadline, Func<DateTimeOffset> clock)
    {
        Runtime = runtime;
        Store = store;
        BatchSize = batchSize;
        Deadline = deadline;
        _clock = clock;
    }

    public IWolverineRuntime Runtime { get; }

    /// <summary>
    ///     The message store this step is working against. For the steps registered in the IoC container,
    ///     this is the main message store
    /// </summary>
    public IMessageStore Store { get; }

    /// <summary>
    ///     The most items this step should handle in one call
    /// </summary>
    public int BatchSize { get; }

    /// <summary>
    ///     The pass starts no new work after this time
    /// </summary>
    public DateTimeOffset Deadline { get; }

    public DateTimeOffset Now => _clock();

    public TimeSpan TimeRemaining
    {
        get
        {
            var remaining = Deadline - _clock();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public bool HasExpired => _clock() >= Deadline;
}

/// <summary>
///     GH-5016. What one call to a durability pass step did
/// </summary>
public class DurabilityPassStepResult
{
    public static DurabilityPassStepResult Nothing => new();

    /// <summary>
    ///     Recovered outgoing messages that were sent
    /// </summary>
    public int OutgoingSent { get; init; }

    /// <summary>
    ///     Recovered outgoing messages whose send failed. They stay in the outbox for a later pass
    /// </summary>
    public int OutgoingFailed { get; init; }

    /// <summary>
    ///     Recovered outgoing messages the transport reported as a serialization failure, such as an SQS message over
    ///     the size limit. No retry can send them, so they were moved to dead letter storage. See GH-5047
    /// </summary>
    public int OutgoingDeadLettered { get; init; }

    /// <summary>
    ///     Scheduled messages that were due and released
    /// </summary>
    public int ScheduledReleased { get; init; }

    /// <summary>
    ///     Incoming messages that were recovered
    /// </summary>
    public int IncomingRecovered { get; init; }

    /// <summary>
    ///     Expired rows that were deleted: handled envelopes, dead letters, deduplication claims
    /// </summary>
    public int ExpiredDeleted { get; init; }

    /// <summary>
    ///     Any other items handled, for steps whose work does not fit the categories above
    /// </summary>
    public int OtherProcessed { get; init; }

    /// <summary>
    ///     Work this step knows is still left after this call
    /// </summary>
    public int Remaining { get; init; }

    /// <summary>
    ///     Everything this call got done
    /// </summary>
    public int Processed => OutgoingSent + OutgoingDeadLettered + ScheduledReleased + IncomingRecovered +
                            ExpiredDeleted + OtherProcessed;

    /// <summary>
    ///     Rows this call failed and added to the step's excluded ids, so calling it again in the same pass
    ///     reaches rows behind them. The runner keeps calling a step that did this even if nothing succeeded
    /// </summary>
    internal int NewlyExcluded { get; set; }

    internal DurabilityPassStepResult Add(DurabilityPassStepResult other)
    {
        return new DurabilityPassStepResult
        {
            OutgoingSent = OutgoingSent + other.OutgoingSent,
            OutgoingFailed = OutgoingFailed + other.OutgoingFailed,
            OutgoingDeadLettered = OutgoingDeadLettered + other.OutgoingDeadLettered,
            ScheduledReleased = ScheduledReleased + other.ScheduledReleased,
            IncomingRecovered = IncomingRecovered + other.IncomingRecovered,
            ExpiredDeleted = ExpiredDeleted + other.ExpiredDeleted,
            OtherProcessed = OtherProcessed + other.OtherProcessed,

            // The latest call knows best what is left
            Remaining = other.Remaining
        };
    }
}

public enum DurabilityPassStepStatus
{
    /// <summary>
    ///     The step ran to completion
    /// </summary>
    Completed,

    /// <summary>
    ///     The step threw an exception
    /// </summary>
    Failed,

    /// <summary>
    ///     The pass ran out of time before it got to this step
    /// </summary>
    NotRun
}

public enum DurabilityPassLockStatus
{
    /// <summary>
    ///     This pass held the store's durability pass lock
    /// </summary>
    Acquired,

    /// <summary>
    ///     Another durability pass already holds the lock for this store, so this pass skipped it
    /// </summary>
    HeldElsewhere,

    /// <summary>
    ///     The store does not take part in durability passes. Only steps registered in the IoC container ran,
    ///     guarded against overlapping passes within this process only
    /// </summary>
    NotSupported,

    /// <summary>
    ///     The pass ran out of time before it got to this store
    /// </summary>
    NotAttempted,

    /// <summary>
    ///     Trying to take the lock threw an exception
    /// </summary>
    Failed
}

/// <summary>
///     GH-5016. What one step did in a durability pass, over every call to it
/// </summary>
public record DurabilityPassStepReport(
    string StepName,
    DurabilityPassStepStatus Status,
    DurabilityPassStepResult Result,
    int Calls,
    TimeSpan Elapsed,
    Exception? Exception);

/// <summary>
///     GH-5016. What a durability pass did against one message store
/// </summary>
public record DurabilityPassStoreReport(
    Uri StoreUri,
    string StoreName,
    DurabilityPassLockStatus Lock,
    IReadOnlyList<DurabilityPassStepReport> Steps,
    Exception? Exception = null);

/// <summary>
///     GH-5016. The result of one durability pass
/// </summary>
public class DurabilityPassReport
{
    internal DurabilityPassReport(DateTimeOffset startedAt, TimeSpan elapsed, bool timedOut,
        IReadOnlyList<DurabilityPassStoreReport> stores)
    {
        StartedAt = startedAt;
        Elapsed = elapsed;
        TimedOut = timedOut;
        Stores = stores;

        var steps = stores.SelectMany(x => x.Steps).ToArray();
        OutgoingSent = steps.Sum(x => x.Result.OutgoingSent);
        OutgoingFailed = steps.Sum(x => x.Result.OutgoingFailed);
        OutgoingDeadLettered = steps.Sum(x => x.Result.OutgoingDeadLettered);
        ScheduledReleased = steps.Sum(x => x.Result.ScheduledReleased);
        IncomingRecovered = steps.Sum(x => x.Result.IncomingRecovered);
        ExpiredDeleted = steps.Sum(x => x.Result.ExpiredDeleted);
        OtherProcessed = steps.Sum(x => x.Result.OtherProcessed);
        Remaining = steps.Sum(x => x.Result.Remaining);
        FailedSteps = steps.Count(x => x.Status == DurabilityPassStepStatus.Failed);
    }

    public DateTimeOffset StartedAt { get; }
    public TimeSpan Elapsed { get; }

    /// <summary>
    ///     The pass hit its <see cref="DurabilityPassOptions.MaxDuration" /> before it got through everything
    /// </summary>
    public bool TimedOut { get; }

    public IReadOnlyList<DurabilityPassStoreReport> Stores { get; }

    public int OutgoingSent { get; }
    public int OutgoingFailed { get; }

    /// <summary>
    ///     Outgoing messages no retry can send, moved to dead letter storage. See GH-5047
    /// </summary>
    public int OutgoingDeadLettered { get; }

    public int ScheduledReleased { get; }
    public int IncomingRecovered { get; }
    public int ExpiredDeleted { get; }
    public int OtherProcessed { get; }

    /// <summary>
    ///     Work the steps reported as still left
    /// </summary>
    public int Remaining { get; }

    public int FailedSteps { get; }

    /// <summary>
    ///     Stores this pass skipped because another durability pass held their lock
    /// </summary>
    public IReadOnlyList<Uri> SkippedStores =>
        Stores.Where(x => x.Lock == DurabilityPassLockStatus.HeldElsewhere).Select(x => x.StoreUri).ToArray();

    /// <summary>
    ///     True when this pass left work behind, so a caller that wants to drain everything should run
    ///     another pass. That includes a store this pass skipped because another pass held its lock: nothing here
    ///     says that store has nothing waiting (GH-5050)
    /// </summary>
    public bool HasMoreWork => TimedOut || Remaining > 0 || SkippedStores.Count > 0;

    public override string ToString()
    {
        return
            $"Durability pass: {OutgoingSent} outgoing sent, {OutgoingFailed} outgoing failed, {OutgoingDeadLettered} outgoing dead lettered, {ScheduledReleased} scheduled released, {IncomingRecovered} incoming recovered, {ExpiredDeleted} expired deleted, {OtherProcessed} other, {Remaining} remaining, {FailedSteps} failed step(s), {SkippedStores.Count} store(s) skipped as locked elsewhere, timed out: {TimedOut}, elapsed {Elapsed}";
    }
}
