using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence.Durability;

// GH-5016: one bounded durability pass, triggered from outside, for DurabilityMode.ServerlessWithOutbox
public class durability_pass_5016 : IAsyncLifetime
{
    private IHost _host = null!;
    private DurabilityPassRunner _runner = null!;
    private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.RecoveryBatchSize = 25;
                opts.Discovery.DisableConventionalDiscovery();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        _runner = new DurabilityPassRunner(_host.GetRuntime()) { Clock = () => _now };
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task<DurabilityPassReport> runAsync(IReadOnlyList<IMessageStore> stores,
        IReadOnlyList<IDurabilityPassStep>? extensionSteps = null, DurabilityPassOptions? options = null,
        CancellationToken? token = null)
    {
        return _runner.RunAsync(stores, extensionSteps ?? [], options ?? new DurabilityPassOptions(),
            token ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task runs_the_steps_registered_in_the_container_from_the_host()
    {
        var step = new RecordingStep("projections", new DurabilityPassStepResult { OtherProcessed = 3 });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddSingleton<IDurabilityPassStep>(step);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds(),
            token: TestContext.Current.CancellationToken);

        step.Calls.Count.ShouldBe(1);

        // Steps from the container run against the main store, here the storeless default
        step.Calls[0].Store.Role.ShouldBe(MessageStoreRole.Main);
        step.Calls[0].BatchSize.ShouldBe(host.GetRuntime().DurabilitySettings.RecoveryBatchSize);

        report.OtherProcessed.ShouldBe(3);
        report.TimedOut.ShouldBeFalse();
        report.HasMoreWork.ShouldBeFalse();

        var store = report.Stores.Single();
        store.Lock.ShouldBe(DurabilityPassLockStatus.NotSupported);
        store.Steps.Single().Status.ShouldBe(DurabilityPassStepStatus.Completed);
    }

    [Theory]
    [InlineData(DurabilityMode.Balanced)]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.Serverless)]
    [InlineData(DurabilityMode.MediatorOnly)]
    public async Task refuses_to_run_outside_serverless_with_outbox(DurabilityMode mode)
    {
        // GH-5037. In these modes the node's own durability agents recover the same rows, under locks the
        // pass does not take, so a pass beside them could send or handle a message twice
        var step = new RecordingStep("projections", new DurabilityPassStepResult { OtherProcessed = 1 });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Services.AddSingleton<IDurabilityPassStep>(step);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(DurabilityMode.ServerlessWithOutbox));
        ex.Message.ShouldContain(mode.ToString());
        step.Calls.ShouldBeEmpty();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task runs_with_nothing_to_do_on_a_storeless_host()
    {
        var report = await _host.RunDurabilityPassAsync(token: TestContext.Current.CancellationToken);

        report.Stores.ShouldBeEmpty();
        report.HasMoreWork.ShouldBeFalse();
    }

    [Fact]
    public async Task runs_each_stores_steps_under_its_lock_and_releases_it()
    {
        var outgoing = new RecordingStep("outgoing", new DurabilityPassStepResult { OutgoingSent = 4, OutgoingFailed = 1 });
        var scheduled = new RecordingStep("scheduled", new DurabilityPassStepResult { ScheduledReleased = 2 });
        var store = new FakePassStore("main", MessageStoreRole.Main, outgoing, scheduled);

        var extension = new RecordingStep("extension", new DurabilityPassStepResult { OtherProcessed = 1 })
        {
            WhileRunning = () => store.LockHeld.ShouldBeTrue()
        };

        var report = await runAsync([store], [extension]);

        store.LockAcquisitions.ShouldBe(1);
        store.LockHeld.ShouldBeFalse();

        outgoing.Calls.Single().Store.ShouldBeSameAs(store);
        scheduled.Calls.Count.ShouldBe(1);
        extension.Calls.Single().Store.ShouldBeSameAs(store);

        var storeReport = report.Stores.Single();
        storeReport.Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
        storeReport.Steps.Select(x => x.StepName).ShouldBe(["outgoing", "scheduled", "extension"]);

        report.OutgoingSent.ShouldBe(4);
        report.OutgoingFailed.ShouldBe(1);
        report.ScheduledReleased.ShouldBe(2);
        report.OtherProcessed.ShouldBe(1);
    }

    [Fact]
    public async Task keeps_calling_a_step_that_excluded_its_failures_so_it_reaches_the_rows_behind_them()
    {
        // A full batch at the head of the outbox that all failed used to end the step for the whole pass, so every
        // pass retried the same failing rows and the healthy rows behind them were never tried
        var outgoing = new RecordingStep("outgoing",
            new DurabilityPassStepResult { OutgoingFailed = 25, NewlyExcluded = 25, Remaining = 10 },
            new DurabilityPassStepResult { OutgoingSent = 10, Remaining = 0 });
        var store = new FakePassStore("main", MessageStoreRole.Main, outgoing);

        var report = await runAsync([store]);

        outgoing.Calls.Count.ShouldBe(2);
        report.OutgoingSent.ShouldBe(10);
    }

    [Fact]
    public async Task stops_calling_a_step_that_neither_processed_nor_excluded_anything()
    {
        var outgoing = new RecordingStep("outgoing", new DurabilityPassStepResult { Remaining = 10 });
        var store = new FakePassStore("main", MessageStoreRole.Main, outgoing);

        await runAsync([store]);

        outgoing.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task the_container_steps_run_once_with_the_main_store_not_with_every_store()
    {
        var main = new FakePassStore("main", MessageStoreRole.Main);
        var ancillary = new FakePassStore("ancillary", MessageStoreRole.Ancillary);
        var extension = new RecordingStep("extension", DurabilityPassStepResult.Nothing);

        var report = await runAsync([ancillary, main], [extension]);

        extension.Calls.Single().Store.ShouldBeSameAs(main);
        report.Stores.Count.ShouldBe(2);
        main.LockAcquisitions.ShouldBe(1);
        ancillary.LockAcquisitions.ShouldBe(1);
    }

    [Fact]
    public async Task skips_a_store_whose_lock_is_held_by_another_pass()
    {
        var outgoing = new RecordingStep("outgoing", new DurabilityPassStepResult { OutgoingSent = 1 });
        var locked = new FakePassStore("locked", MessageStoreRole.Ancillary, outgoing) { LockAvailable = false };

        var otherStep = new RecordingStep("other", new DurabilityPassStepResult { OutgoingSent = 1 });
        var other = new FakePassStore("other", MessageStoreRole.Main, otherStep);

        var report = await runAsync([locked, other]);

        // Skipped without waiting, and nothing sent twice
        outgoing.Calls.ShouldBeEmpty();
        otherStep.Calls.Count.ShouldBe(1);

        report.Stores[0].Lock.ShouldBe(DurabilityPassLockStatus.HeldElsewhere);
        report.Stores[0].Steps.ShouldBeEmpty();
        report.SkippedStores.ShouldBe([locked.Uri]);
        report.OutgoingSent.ShouldBe(1);

        // GH-5050. Nothing says the skipped store has nothing waiting, so a drain loop has to come back
        report.Remaining.ShouldBe(0);
        report.HasMoreWork.ShouldBeTrue();
    }

    [Fact]
    public async Task an_overlapping_pass_in_the_same_process_skips_the_store_instead_of_waiting()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = new RecordingStep("slow", new DurabilityPassStepResult { OutgoingSent = 1 })
        {
            WhileRunningAsync = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };

        var store = new FakePassStore("main", MessageStoreRole.Main, slow);

        var first = runAsync([store]);
        await started.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var second = await runAsync([store]);
        second.Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.HeldElsewhere);
        second.OutgoingSent.ShouldBe(0);
        second.HasMoreWork.ShouldBeTrue();

        release.SetResult();
        var firstReport = await first.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
        firstReport.OutgoingSent.ShouldBe(1);
        slow.Calls.Count.ShouldBe(1);

        // And once the first pass is done, the store is free again
        var third = await runAsync([store]);
        third.Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
    }

    [Fact]
    public async Task calls_a_step_again_while_it_reports_work_left_and_makes_progress()
    {
        var step = new RecordingStep("outgoing",
            new DurabilityPassStepResult { OutgoingSent = 10, Remaining = 15 },
            new DurabilityPassStepResult { OutgoingSent = 10, Remaining = 5 },
            new DurabilityPassStepResult { OutgoingSent = 5, Remaining = 0 });

        var store = new FakePassStore("main", MessageStoreRole.Main, step);

        var report = await runAsync([store], options: new DurabilityPassOptions { BatchSize = 10 });

        step.Calls.Count.ShouldBe(3);
        step.Calls.ShouldAllBe(x => x.BatchSize == 10);

        var stepReport = report.Stores.Single().Steps.Single();
        stepReport.Calls.ShouldBe(3);
        stepReport.Result.OutgoingSent.ShouldBe(25);
        stepReport.Result.Remaining.ShouldBe(0);

        report.OutgoingSent.ShouldBe(25);
        report.HasMoreWork.ShouldBeFalse();
    }

    [Fact]
    public async Task batch_size_defaults_to_the_recovery_batch_size()
    {
        var step = new RecordingStep("outgoing", DurabilityPassStepResult.Nothing);
        await runAsync([new FakePassStore("main", MessageStoreRole.Main, step)]);

        step.Calls.Single().BatchSize.ShouldBe(25);
    }

    [Fact]
    public async Task does_not_call_a_step_again_when_it_made_no_progress()
    {
        // Every send failed: calling it again in this pass would just spin
        var step = new RecordingStep("outgoing",
            new DurabilityPassStepResult { OutgoingFailed = 10, Remaining = 10 },
            new DurabilityPassStepResult { OutgoingSent = 10 });

        var report = await runAsync([new FakePassStore("main", MessageStoreRole.Main, step)]);

        step.Calls.Count.ShouldBe(1);
        report.OutgoingFailed.ShouldBe(10);
        report.Remaining.ShouldBe(10);
        report.TimedOut.ShouldBeFalse();
        report.HasMoreWork.ShouldBeTrue();
    }

    [Fact]
    public async Task stops_starting_work_at_the_max_duration_and_reports_what_is_left()
    {
        var first = new RecordingStep("first",
            new DurabilityPassStepResult { OutgoingSent = 10, Remaining = 50 },
            new DurabilityPassStepResult { OutgoingSent = 10, Remaining = 40 })
        {
            // Each batch takes 20 seconds of a 30 second pass
            WhileRunning = () => _now = _now.AddSeconds(20)
        };

        var second = new RecordingStep("second", new DurabilityPassStepResult { ScheduledReleased = 1 });
        var main = new FakePassStore("main", MessageStoreRole.Main, first, second);

        var ancillaryStep = new RecordingStep("ancillary", new DurabilityPassStepResult { ScheduledReleased = 1 });
        var ancillary = new FakePassStore("ancillary", MessageStoreRole.Ancillary, ancillaryStep);

        var report = await runAsync([main, ancillary], options: new DurabilityPassOptions { MaxDuration = 30.Seconds() });

        // The batch that crossed the deadline was allowed to finish, then nothing new started
        first.Calls.Count.ShouldBe(2);
        first.Calls[0].TimeRemaining.ShouldBe(30.Seconds());
        second.Calls.ShouldBeEmpty();
        ancillaryStep.Calls.ShouldBeEmpty();
        ancillary.LockAcquisitions.ShouldBe(0);

        report.TimedOut.ShouldBeTrue();
        report.HasMoreWork.ShouldBeTrue();
        report.OutgoingSent.ShouldBe(20);
        report.Remaining.ShouldBe(40);

        report.Stores[0].Steps.Select(x => x.Status)
            .ShouldBe([DurabilityPassStepStatus.Completed, DurabilityPassStepStatus.NotRun]);
        report.Stores[1].Lock.ShouldBe(DurabilityPassLockStatus.NotAttempted);
        report.Stores[1].Steps.Single().Status.ShouldBe(DurabilityPassStepStatus.NotRun);

        // The main store's lock was released even though the pass ran out of time
        main.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task a_failing_step_is_reported_and_the_rest_of_the_pass_carries_on()
    {
        var failing = new RecordingStep("failing", DurabilityPassStepResult.Nothing)
        {
            WhileRunning = () => throw new DivideByZeroException()
        };
        var after = new RecordingStep("after", new DurabilityPassStepResult { IncomingRecovered = 2 });
        var store = new FakePassStore("main", MessageStoreRole.Main, failing, after);

        var report = await runAsync([store]);

        var steps = report.Stores.Single().Steps;
        steps[0].Status.ShouldBe(DurabilityPassStepStatus.Failed);
        steps[0].Exception.ShouldBeOfType<DivideByZeroException>();
        steps[1].Status.ShouldBe(DurabilityPassStepStatus.Completed);

        report.FailedSteps.ShouldBe(1);
        report.IncomingRecovered.ShouldBe(2);
        store.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task a_lock_that_cannot_be_taken_is_reported_and_its_steps_are_not_run()
    {
        var step = new RecordingStep("outgoing", new DurabilityPassStepResult { OutgoingSent = 1 });
        var store = new FakePassStore("main", MessageStoreRole.Main, step) { LockFailure = new TimeoutException() };

        var report = await runAsync([store]);

        step.Calls.ShouldBeEmpty();
        var storeReport = report.Stores.Single();
        storeReport.Lock.ShouldBe(DurabilityPassLockStatus.Failed);
        storeReport.Exception.ShouldBeOfType<TimeoutException>();
        storeReport.Steps.Single().Status.ShouldBe(DurabilityPassStepStatus.NotRun);

        // And the in-process guard was let go, so the next pass can try again
        store.LockFailure = null;
        (await runAsync([store])).Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
    }

    [Fact]
    public async Task cancellation_stops_the_pass_and_releases_the_lock()
    {
        using var cancellation = new CancellationTokenSource();
        var step = new RecordingStep("cancelling", new DurabilityPassStepResult { OutgoingSent = 1, Remaining = 1 },
            DurabilityPassStepResult.Nothing)
        {
            WhileRunning = () => cancellation.Cancel()
        };
        var store = new FakePassStore("main", MessageStoreRole.Main, step);

        await Should.ThrowAsync<OperationCanceledException>(() => runAsync([store], token: cancellation.Token));

        store.LockHeld.ShouldBeFalse();
        (await runAsync([store])).Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
    }
}

public class RecordingStep : IDurabilityPassStep
{
    private readonly Queue<DurabilityPassStepResult> _results;
    private readonly DurabilityPassStepResult _last;

    public RecordingStep(string name, params DurabilityPassStepResult[] results)
    {
        Name = name;
        _results = new Queue<DurabilityPassStepResult>(results);
        _last = results.Last();
    }

    public string Name { get; }

    public List<CallRecord> Calls { get; } = new();

    public Action? WhileRunning { get; init; }
    public Func<Task>? WhileRunningAsync { get; init; }

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        Calls.Add(new CallRecord(context.Store, context.BatchSize, context.TimeRemaining));

        WhileRunning?.Invoke();
        if (WhileRunningAsync != null) await WhileRunningAsync();

        return _results.Count > 0 ? _results.Dequeue() : _last;
    }

    public record CallRecord(IMessageStore Store, int BatchSize, TimeSpan TimeRemaining);
}

// Re-implements IMessageStore so each fake gets its own identity on top of the null store
public class FakePassStore : NullMessageStore, IMessageStore, IMessageStoreWithDurabilityPass
{
    private readonly IDurabilityPassStep[] _steps;

    public FakePassStore(string name, MessageStoreRole role, params IDurabilityPassStep[] steps)
    {
        Name = name;
        Role = role;
        Uri = new Uri($"wolverinedb://fake/{name}");
        _steps = steps;
    }

    public new string Name { get; }
    public new MessageStoreRole Role { get; }
    public new Uri Uri { get; }

    public bool LockAvailable { get; set; } = true;
    public bool SupportsTransactionScopedDurabilityPassLock { get; set; }
    public Exception? LockFailure { get; set; }
    public bool LockHeld { get; private set; }
    public int LockAcquisitions { get; private set; }

    public Task<IAsyncDisposable?> TryAcquireDurabilityPassLockAsync(CancellationToken token)
    {
        if (LockFailure != null) throw LockFailure;
        if (!LockAvailable || LockHeld) return Task.FromResult<IAsyncDisposable?>(null);

        LockHeld = true;
        LockAcquisitions++;
        return Task.FromResult<IAsyncDisposable?>(new Releaser(this));
    }

    public IReadOnlyList<IDurabilityPassStep> BuildDurabilityPassSteps(IWolverineRuntime runtime)
    {
        return _steps;
    }

    private class Releaser(FakePassStore store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            store.LockHeld = false;
            return ValueTask.CompletedTask;
        }
    }
}
