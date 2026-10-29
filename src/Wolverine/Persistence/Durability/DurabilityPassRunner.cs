using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5016. Runs one bounded durability pass across every message store of one Wolverine runtime
/// </summary>
internal class DurabilityPassRunner
{
    private static readonly ConditionalWeakTable<IWolverineRuntime, DurabilityPassRunner> _perRuntime = new();

    public static DurabilityPassRunner For(IWolverineRuntime runtime)
    {
        return _perRuntime.GetValue(runtime, r => new DurabilityPassRunner(r));
    }

    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;

    // Overlapping passes in this process skip a store without asking the database, and this is the only
    // guard at all for a store that has no database lock of its own
    private readonly ConcurrentDictionary<Uri, SemaphoreSlim> _gates = new();

    // The stores already warned about DurabilitySettings.UseTransactionScopedDurabilityPassLock, so each warning is
    // written once per process rather than on every pass
    private readonly ConcurrentDictionary<Uri, bool> _lockWarnings = new();

    internal DurabilityPassRunner(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<DurabilityPassRunner>();
    }

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    ///     GH-5037. In any other mode the node's own durability agents recover the same rows, under locks the pass
    ///     does not take, so a pass beside them could send or handle a message twice
    /// </summary>
    public static bool IsAllowedIn(DurabilityMode mode) => mode == DurabilityMode.ServerlessWithOutbox;

    public static void AssertPassIsAllowed(DurabilityMode mode)
    {
        if (!IsAllowedIn(mode))
        {
            throw new InvalidOperationException(
                $"A durability pass can only run under DurabilityMode.{nameof(DurabilityMode.ServerlessWithOutbox)}, but this application uses DurabilityMode.{mode}. In that mode Wolverine's own durability agents already do this work, and a pass beside them could send or handle a message twice.");
        }
    }

    public async Task<DurabilityPassReport> RunAsync(DurabilityPassOptions options, CancellationToken token)
    {
        AssertPassIsAllowed(_runtime.Options.Durability.Mode);

        var stores = await _runtime.Stores.FindAllAsync().ConfigureAwait(false);
        var extensionSteps = _runtime.Services.GetServices<IDurabilityPassStep>().ToArray();

        return await RunAsync(stores, extensionSteps, options, token).ConfigureAwait(false);
    }

    internal async Task<DurabilityPassReport> RunAsync(IReadOnlyList<IMessageStore> stores,
        IReadOnlyList<IDurabilityPassStep> extensionSteps, DurabilityPassOptions options, CancellationToken token)
    {
        var startedAt = Clock();
        var deadline = startedAt.Add(options.MaxDuration);
        var batchSize = options.BatchSize is > 0 ? options.BatchSize.Value : _runtime.DurabilitySettings.RecoveryBatchSize;
        var stopwatch = Stopwatch.StartNew();

        // The steps registered in the container run once per pass, under the main store's lock
        var main = stores.FirstOrDefault(x => x.Role == MessageStoreRole.Main);
        if (main == null && extensionSteps.Count > 0)
        {
            main = _runtime.Stores.Main;
            stores = stores.Append(main).ToArray();
        }

        var state = new PassState();
        var reports = new List<DurabilityPassStoreReport>();

        foreach (var store in stores)
        {
            token.ThrowIfCancellationRequested();

            var steps = new List<IDurabilityPassStep>();
            if (store is IMessageStoreWithDurabilityPass passStore)
            {
                steps.AddRange(passStore.BuildDurabilityPassSteps(_runtime));
            }

            if (ReferenceEquals(store, main))
            {
                steps.AddRange(extensionSteps);
            }

            warnAboutTheTransactionScopedLockOnce(store, ReferenceEquals(store, main) ? extensionSteps : []);

            if (store is not IMessageStoreWithDurabilityPass && steps.Count == 0)
            {
                reports.Add(new DurabilityPassStoreReport(store.Uri, store.Name, DurabilityPassLockStatus.NotSupported, []));
                continue;
            }

            if (Clock() >= deadline)
            {
                state.TimedOut = true;
                reports.Add(new DurabilityPassStoreReport(store.Uri, store.Name, DurabilityPassLockStatus.NotAttempted,
                    steps.Select(notRun).ToArray()));
                continue;
            }

            reports.Add(await runStoreAsync(store, steps, batchSize, deadline, state, token).ConfigureAwait(false));
        }

        var report = new DurabilityPassReport(startedAt, stopwatch.Elapsed, state.TimedOut, reports);
        _logger.LogInformation("{Report}", report);

        return report;
    }

    private async Task<DurabilityPassStoreReport> runStoreAsync(IMessageStore store, List<IDurabilityPassStep> steps,
        int batchSize, DateTimeOffset deadline, PassState state, CancellationToken token)
    {
        var gate = _gates.GetOrAdd(store.Uri, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping message store {Store} in this durability pass because another pass in this process is running against it", store.Uri);
            return new DurabilityPassStoreReport(store.Uri, store.Name, DurabilityPassLockStatus.HeldElsewhere, []);
        }

        try
        {
            IAsyncDisposable? databaseLock = null;
            var lockStatus = DurabilityPassLockStatus.NotSupported;

            if (store is IMessageStoreWithDurabilityPass passStore)
            {
                try
                {
                    databaseLock = await passStore.TryAcquireDurabilityPassLockAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to take the durability pass lock for message store {Store}", store.Uri);
                    return new DurabilityPassStoreReport(store.Uri, store.Name, DurabilityPassLockStatus.Failed,
                        steps.Select(notRun).ToArray(), e);
                }

                if (databaseLock == null)
                {
                    _logger.LogDebug("Skipping message store {Store} in this durability pass because another pass holds its lock", store.Uri);
                    return new DurabilityPassStoreReport(store.Uri, store.Name, DurabilityPassLockStatus.HeldElsewhere, []);
                }

                lockStatus = DurabilityPassLockStatus.Acquired;
            }

            try
            {
                var stepReports = new List<DurabilityPassStepReport>();
                foreach (var step in steps)
                {
                    token.ThrowIfCancellationRequested();

                    if (Clock() >= deadline)
                    {
                        state.TimedOut = true;
                        stepReports.Add(notRun(step));
                        continue;
                    }

                    stepReports.Add(await runStepAsync(store, step, batchSize, deadline, state, token).ConfigureAwait(false));
                }

                return new DurabilityPassStoreReport(store.Uri, store.Name, lockStatus, stepReports);
            }
            finally
            {
                if (databaseLock != null)
                {
                    try
                    {
                        await databaseLock.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error releasing the durability pass lock for message store {Store}", store.Uri);
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DurabilityPassStepReport> runStepAsync(IMessageStore store, IDurabilityPassStep step,
        int batchSize, DateTimeOffset deadline, PassState state, CancellationToken token)
    {
        var context = new DurabilityPassContext(_runtime, store, batchSize, deadline, Clock);
        var total = DurabilityPassStepResult.Nothing;
        var calls = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                var result = await step.ExecuteAsync(context, token).ConfigureAwait(false);
                calls++;
                total = total.Add(result);

                // Done, stuck, or out of time. A step that reports work left but got nothing done this
                // time is not called again until the next pass, unless it excluded the rows that failed:
                // then the next call reaches the rows behind them, up to MaxExcludedIds
                if (result.Remaining <= 0 || (result.Processed <= 0 && result.NewlyExcluded <= 0)) break;

                if (context.HasExpired)
                {
                    state.TimedOut = true;
                    break;
                }

                token.ThrowIfCancellationRequested();
            }

            return new DurabilityPassStepReport(step.Name, DurabilityPassStepStatus.Completed, total, calls,
                stopwatch.Elapsed, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Durability pass step {Step} failed against message store {Store}", step.Name, store.Uri);
            return new DurabilityPassStepReport(step.Name, DurabilityPassStepStatus.Failed, total, calls,
                stopwatch.Elapsed, e);
        }
    }

    private void warnAboutTheTransactionScopedLockOnce(IMessageStore store,
        IReadOnlyList<IDurabilityPassStep> extensionSteps)
    {
        if (!_runtime.DurabilitySettings.UseTransactionScopedDurabilityPassLock) return;
        if (store is not IMessageStoreWithDurabilityPass passStore) return;
        if (!_lockWarnings.TryAdd(store.Uri, true)) return;

        if (!passStore.SupportsTransactionScopedDurabilityPassLock)
        {
            _logger.LogWarning(
                "DurabilitySettings.UseTransactionScopedDurabilityPassLock is on, but message store {Store} does not support a transaction-scoped durability pass lock and takes its usual lock instead. Only the PostgreSQL message store honors this setting",
                store.Uri);
            return;
        }

        if (extensionSteps.Count > 0)
        {
            // GH-5279. Marten's gap detection counts the lock's open transaction as a possible reserver of any
            // sequence gap first seen after the pass started, so a step that waits for projections to catch up can
            // wait on its own pass until the pass runs out of time
            _logger.LogWarning(
                "The durability pass steps {Steps} run against message store {Store} while its transaction-scoped durability pass lock holds an open transaction. A step that waits for Marten's asynchronous projections to catch up cannot get past an event sequence gap while that transaction is open, and will wait until the pass runs out of time",
                string.Join(", ", extensionSteps.Select(x => x.Name)), store.Uri);
        }
    }

    private class PassState
    {
        public bool TimedOut { get; set; }
    }

    private static DurabilityPassStepReport notRun(IDurabilityPassStep step)
    {
        return new DurabilityPassStepReport(step.Name, DurabilityPassStepStatus.NotRun, DurabilityPassStepResult.Nothing,
            0, TimeSpan.Zero, null);
    }
}
