using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// Exploratory tests for PR #5044 (DurabilityMode.ServerlessWithOutbox): the risks the PR body flags for
/// reviewers, measured against a real database. Several of these are EXPECTED TO FAIL on the PR branch; each
/// failure message carries the measured numbers. Measurements also go to the test output.
/// </summary>
public abstract class DurabilityPassExploratory5044Compliance : IAsyncLifetime
{
    protected const string StepOutgoing = "Recover stale outgoing messages";
    protected const string StepIncoming = "Recover incoming messages";
    protected const string StepExpired = "Delete expired rows";

    private readonly List<IHost> _hosts = new();

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected IHost theHost = null!;

    /// <summary>
    /// The schema this suite owns. Prefix it with explore5044_flags
    /// </summary>
    protected abstract string SchemaName { get; }

    protected abstract void ConfigurePersistence(WolverineOptions opts, string schemaName);

    /// <summary>
    /// Kill the database session behind <paramref name="connection" /> from another session, the way the
    /// server sees a process that died while holding the durability pass lock
    /// </summary>
    protected abstract Task KillSessionAsync(DbConnection connection);

    protected virtual string Quote(string column) => $"\"{column}\"";

    public async ValueTask InitializeAsync()
    {
        ControlledTransport.Reset();
        SlowInboxHandler.Reset();
        RequeueingInboxHandler.Reset();

        theHost = await BuildHostAsync();
        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ControlledTransport.ReleaseHangs();
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            catch
            {
                // best effort
            }

            host.Dispose();
        }
    }

    protected async Task<IHost> BuildHostAsync(Action<WolverineOptions>? configure = null)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<SlowInboxHandler>()
                    .IncludeType<RequeueingInboxHandler>();
                opts.OnException<RequeueingInboxException>().Requeue();
                opts.Transports.GetOrCreate<ControlledTransport>();
                ConfigurePersistence(opts, SchemaName);
                configure?.Invoke(opts);
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    protected IMessageDatabase DatabaseFor(IHost host) => host.GetRuntime().Storage.As<IMessageDatabase>();
    protected IMessageStore StoreFor(IHost host) => host.GetRuntime().Storage;

    protected static void Log(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        Console.WriteLine("[5044] " + message);
    }

    protected async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        var database = DatabaseFor(theHost);
        await using var conn = await database.DataSource.OpenConnectionAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteNonQueryAsync(Token);
    }

    protected async Task<long> ScalarAsync(string sql)
    {
        var database = DatabaseFor(theHost);
        await using var conn = await database.DataSource.OpenConnectionAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Token));
    }

    protected string Outgoing => DatabaseFor(theHost).TableNameFor(DatabaseConstants.OutgoingTable);
    protected string Incoming => DatabaseFor(theHost).TableNameFor(DatabaseConstants.IncomingTable);

    protected Task<long> CountOutgoingAsync() => ScalarAsync($"select count(*) from {Outgoing}");

    protected Task<long> CountIncomingAsync(EnvelopeStatus status) =>
        ScalarAsync($"select count(*) from {Incoming} where {DatabaseConstants.Status} = '{status}'");

    /// <summary>
    /// Store <paramref name="count" /> owner-0 outbox rows for ctl://{destination}, all with the same written
    /// time <paramref name="age" /> ago, so the pass sees them as stale and in that order relative to other groups
    /// </summary>
    protected async Task<Envelope[]> StoreStaleOutgoingAsync(string destination, int count, TimeSpan age)
    {
        var uri = $"ctl://{destination}".ToUri();
        var envelopes = Enumerable.Range(0, count).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = uri;
            envelope.DeliverBy = null;
            return envelope;
        }).ToArray();

        foreach (var chunk in envelopes.Chunk(100))
        {
            await StoreFor(theHost).Outbox.StoreOutgoingAsync(chunk, 0);
        }

        await ExecuteAsync(
            $"update {Outgoing} set {Quote(DatabaseConstants.Timestamp)} = @time where {DatabaseConstants.Destination} = @destination",
            ("time", DateTimeOffset.UtcNow.Subtract(age)), ("destination", uri.ToString()));

        return envelopes;
    }

    protected static DurabilityPassStepReport StepOf(DurabilityPassReport report, string step) =>
        report.Stores.Single(x => x.Steps.Any(s => s.StepName == step)).Steps.Single(x => x.StepName == step);

    protected static string Describe(DurabilityPassReport report)
    {
        var steps = report.Stores.SelectMany(s => s.Steps.Select(x =>
            $"{x.StepName}: {x.Status} calls={x.Calls} processed={x.Result.Processed} failed={x.Result.OutgoingFailed} remaining={x.Result.Remaining}"));
        var locks = report.Stores.Select(s => $"{s.StoreUri}={s.Lock}");
        return $"{report} | locks: {locks.Join(", ")} | {steps.Join(" ; ")}";
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. Duplicate handling
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Host A receives a message on a durable listener and its handler runs 6 s. InboxStaleTime is 2 s. Host B
    /// runs a pass 3 s in. The PR body says the pass can start the message a second time
    /// </summary>
    [Fact]
    public async Task s1_a_handler_still_running_on_another_host_is_not_started_again_by_a_pass()
    {
        SlowInboxHandler.Duration = 6.Seconds();

        var hostA = await BuildHostAsync(opts =>
        {
            opts.Durability.InboxStaleTime = 2.Seconds();
            opts.ListenForMessagesFrom("ctl://inbox");
        });
        var hostB = await BuildHostAsync(opts => opts.Durability.InboxStaleTime = 2.Seconds());

        var runtimeA = hostA.GetRuntime();
        var endpoint = runtimeA.Options.Transports.GetOrCreate<ControlledTransport>().Endpoints["inbox"];
        endpoint.Mode.ShouldBe(EndpointMode.Durable);

        // Neither serverless mode starts listeners for you (Endpoint.ShouldAutoStartAsListener), so start the
        // durable listener that the serverless guide's InboxStaleTime section describes by hand
        endpoint.Listener.ShouldBeNull();
        await runtimeA.Endpoints.StartListenerAsync(endpoint, Token);
        endpoint.Listener.ShouldNotBeNull();

        var envelope = new Envelope(new SlowInboxMessage(Guid.NewGuid()))
        {
            Destination = "ctl://inbox".ToUri(),
            MessageType = typeof(SlowInboxMessage).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType
        };
        envelope.Data = runtimeA.Options.DefaultSerializer.Write(envelope);

        await endpoint.Listener!.DeliverAsync(envelope);

        // Wait for host A's handler to start, then for the stale clock to run out
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (SlowInboxHandler.Started == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50, Token);
        SlowInboxHandler.Started.ShouldBe(1);
        await Task.Delay(3.Seconds(), Token);

        var report = await hostB.RunDurabilityPassAsync(30.Seconds(), Token);
        Log("s1 pass on host B: " + Describe(report));

        // Let host A's copy finish
        deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (SlowInboxHandler.Running > 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(100, Token);

        Log($"s1 started={SlowInboxHandler.Started} completed={SlowInboxHandler.Completed} maxConcurrent={SlowInboxHandler.MaxConcurrent} incomingRecovered={report.IncomingRecovered}");

        SlowInboxHandler.Started.ShouldBe(1,
            $"started {SlowInboxHandler.Started} time(s), {SlowInboxHandler.MaxConcurrent} at once; the pass recovered {report.IncomingRecovered} incoming row(s) while host A's handler was still running");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Starvation
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="failing" /> stale rows for a destination that always fails, written before 10 healthy
    /// rows. Five passes at the default batch size of 100
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    [InlineData(600)]
    public async Task s2_healthy_rows_behind_failing_rows_are_sent_within_five_passes(int failing)
    {
        ControlledTransport.Behavior("down").Fail = true;

        await StoreStaleOutgoingAsync("down", failing, 2.Hours());
        var healthy = await StoreStaleOutgoingAsync("healthy", 10, 1.Hours());

        var perPass = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
            {
                MaxDuration = 1.Minutes(), BatchSize = 100
            }, Token);

            var step = StepOf(report, StepOutgoing);
            var line =
                $"pass {i}: calls={step.Calls} sent={step.Result.OutgoingSent} failed={step.Result.OutgoingFailed} remaining={step.Result.Remaining} hasMoreWork={report.HasMoreWork} healthySentSoFar={ControlledTransport.Behavior("healthy").Sent.Count}";
            perPass.Add(line);
            Log($"s2 failing={failing} {line}");
        }

        var sentHealthy = healthy.Count(x => ControlledTransport.Behavior("healthy").Sent.ContainsKey(x.Id));
        sentHealthy.ShouldBe(10,
            $"with {failing} failing rows ahead, {sentHealthy}/10 healthy rows were sent in 5 passes; send attempts on the failing destination={ControlledTransport.Behavior("down").Attempts}; {perPass.Join(" | ")}");
    }

    /// <summary>
    /// The inbox steps share the runner's loop. 150 due scheduled rows whose handler always asks to be requeued,
    /// due before 10 healthy ones
    /// </summary>
    [Fact]
    public async Task s2_healthy_scheduled_rows_behind_requeueing_rows_are_released_within_five_passes()
    {
        var runtime = theHost.GetRuntime();
        Envelope scheduled(bool fail, DateTimeOffset dueAt)
        {
            var envelope = new Envelope(new RequeueingInboxMessage(Guid.NewGuid(), fail))
            {
                Destination = "ctl://inbox".ToUri(),
                MessageType = typeof(RequeueingInboxMessage).ToMessageTypeName(),
                ContentType = EnvelopeConstants.JsonContentType,
                Status = EnvelopeStatus.Scheduled,
                ScheduledTime = dueAt,
                OwnerId = 0
            };
            envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
            return envelope;
        }

        var failing = Enumerable.Range(0, 150).Select(i => scheduled(true, DateTimeOffset.UtcNow.Subtract(2.Hours()).AddSeconds(i))).ToArray();
        var healthy = Enumerable.Range(0, 10).Select(i => scheduled(false, DateTimeOffset.UtcNow.Subtract(1.Hours()).AddSeconds(i))).ToArray();
        foreach (var chunk in failing.Concat(healthy).Chunk(50))
        {
            await StoreFor(theHost).Inbox.StoreIncomingAsync(chunk);
        }

        var lines = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
            {
                MaxDuration = 1.Minutes(), BatchSize = 100
            }, Token);
            var step = StepOf(report, "Release due scheduled messages");
            var line = $"pass {i}: calls={step.Calls} released={step.Result.ScheduledReleased} remaining={step.Result.Remaining} hasMoreWork={report.HasMoreWork} healthyHandled={RequeueingInboxHandler.Healthy}";
            lines.Add(line);
            Log("s2 inbox " + line);
        }

        RequeueingInboxHandler.Healthy.ShouldBe(10,
            $"{RequeueingInboxHandler.Healthy}/10 healthy scheduled rows released in 5 passes behind 150 requeueing ones; failing attempts={RequeueingInboxHandler.FailingAttempts}; {lines.Join(" | ")}");
    }

    /// <summary>
    /// A caller draining with "while (report.HasMoreWork) run another pass" when every row left is one that
    /// keeps failing
    /// </summary>
    [Fact]
    public async Task s2_a_drain_loop_on_HasMoreWork_ends_when_only_failing_rows_are_left()
    {
        ControlledTransport.Behavior("down").Fail = true;
        await StoreStaleOutgoingAsync("down", 150, 2.Hours());

        var passes = 0;
        DurabilityPassReport report;
        do
        {
            report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
            {
                MaxDuration = 1.Minutes(), BatchSize = 100
            }, Token);
            passes++;
        } while (report.HasMoreWork && passes < 10);

        Log($"s2 drain loop: passes={passes} last={Describe(report)} attempts={ControlledTransport.Behavior("down").Attempts}");

        report.HasMoreWork.ShouldBeFalse(
            $"after {passes} passes HasMoreWork is still true (Remaining={report.Remaining}, OutgoingFailed={report.OutgoingFailed}) although only rows that always fail are left; send attempts={ControlledTransport.Behavior("down").Attempts}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Overlapping passes
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task s3_overlapping_passes_on_one_host_skip_and_never_send_twice()
    {
        ControlledTransport.Behavior("slow").Delay = 20.Milliseconds();
        var rows = await StoreStaleOutgoingAsync("slow", 100, 2.Hours());

        var first = theHost.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 1.Minutes() }, Token);
        await Task.Delay(300, Token);
        var stopwatch = Stopwatch.StartNew();
        var second = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 1.Minutes() }, Token);
        var secondElapsed = stopwatch.Elapsed;
        var firstReport = await first;

        var sent = ControlledTransport.Behavior("slow").Sent;
        Log($"s3 same host: first={Describe(firstReport)}");
        Log($"s3 same host: second (took {secondElapsed.TotalMilliseconds:N0} ms)={Describe(second)}");
        Log($"s3 same host: distinct sent={sent.Count} duplicates={sent.Values.Count(x => x > 1)} outboxLeft={await CountOutgoingAsync()}");

        second.SkippedStores.Count.ShouldBe(1);
        sent.Values.ShouldAllBe(x => x == 1);
        rows.All(x => sent.ContainsKey(x.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task s3_overlapping_passes_on_two_hosts_skip_and_never_send_twice()
    {
        ControlledTransport.Behavior("slow").Delay = 20.Milliseconds();
        var rows = await StoreStaleOutgoingAsync("slow", 100, 2.Hours());
        var other = await BuildHostAsync();

        var first = theHost.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 1.Minutes() }, Token);
        await Task.Delay(300, Token);
        var stopwatch = Stopwatch.StartNew();
        var second = await other.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 1.Minutes() }, Token);
        var secondElapsed = stopwatch.Elapsed;
        var firstReport = await first;

        var sent = ControlledTransport.Behavior("slow").Sent;
        Log($"s3 two hosts: first={Describe(firstReport)}");
        Log($"s3 two hosts: second (took {secondElapsed.TotalMilliseconds:N0} ms)={Describe(second)}");
        Log($"s3 two hosts: distinct sent={sent.Count} duplicates={sent.Values.Count(x => x > 1)}");

        second.SkippedStores.Count.ShouldBe(1);
        second.Stores.Single().Lock.ShouldBe(DurabilityPassLockStatus.HeldElsewhere);
        sent.Values.ShouldAllBe(x => x == 1);
        rows.All(x => sent.ContainsKey(x.Id)).ShouldBeTrue();
    }

    /// <summary>
    /// A pass that skipped the store did none of the work, while rows are waiting. Does its report tell a
    /// drain loop or a scheduler to come back?
    /// </summary>
    [Fact]
    public async Task s3_a_skipped_pass_reports_that_work_is_left()
    {
        await StoreStaleOutgoingAsync("healthy", 5, 2.Hours());

        var passStore = StoreFor(theHost).As<IMessageStoreWithDurabilityPass>();
        await using var held = await passStore.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        var other = await BuildHostAsync();
        var report = await other.RunDurabilityPassAsync(30.Seconds(), Token);
        Log($"s3 skipped pass: {Describe(report)} outboxRows={await CountOutgoingAsync()}");

        report.SkippedStores.Count.ShouldBe(1);
        report.HasMoreWork.ShouldBeTrue(
            $"a pass that skipped the only store reports HasMoreWork={report.HasMoreWork}, Remaining={report.Remaining}, TimedOut={report.TimedOut}, FailedSteps={report.FailedSteps} while {await CountOutgoingAsync()} stale outbox rows are waiting");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. Pass bounds
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task s4_max_duration_is_honored_with_a_slow_broker()
    {
        // 500 ms per send, one batch of 20 is 10 s of sends
        ControlledTransport.Behavior("slow").Delay = 500.Milliseconds();
        await StoreStaleOutgoingAsync("slow", 30, 2.Hours());

        var stopwatch = Stopwatch.StartNew();
        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 1.Seconds(), BatchSize = 20
        }, Token);
        var elapsed = stopwatch.Elapsed;

        Log($"s4 slow broker: elapsed={elapsed.TotalMilliseconds:N0} ms for MaxDuration=1000 ms; {Describe(report)}");

        elapsed.ShouldBeLessThan(3.Seconds(),
            $"a pass with MaxDuration 1 s took {elapsed.TotalMilliseconds:N0} ms; sent {report.OutgoingSent}, timedOut={report.TimedOut}");
    }

    [Fact]
    public async Task s4_cancelling_a_pass_stops_a_hung_send()
    {
        ControlledTransport.Behavior("hung").Hang = true;
        await StoreStaleOutgoingAsync("hung", 3, 2.Hours());

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.CancelAfter(2.Seconds());

        var stopwatch = Stopwatch.StartNew();
        var pass = theHost.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 1.Seconds() },
            cancellation.Token);

        var finished = await Task.WhenAny(pass, Task.Delay(15.Seconds(), Token));
        var elapsed = stopwatch.Elapsed;
        Log($"s4 hung send: pass finished={finished == pass} after {elapsed.TotalMilliseconds:N0} ms (MaxDuration 1 s, token cancelled at 2 s), status={pass.Status}");

        // Release it so the host can stop
        ControlledTransport.ReleaseHangs();

        (finished == pass).ShouldBeTrue(
            $"a pass with MaxDuration 1 s whose token was cancelled at 2 s was still running after {elapsed.TotalSeconds:N0} s, held up by a broker send that never answers");
    }

    [Fact]
    public async Task s4_batch_size_is_honored_and_HasMoreWork_drains_everything_once()
    {
        ControlledTransport.Behavior("slow").Delay = 30.Milliseconds();
        var rows = await StoreStaleOutgoingAsync("slow", 45, 2.Hours());

        var passes = 0;
        var calls = 0;
        DurabilityPassReport report;
        do
        {
            report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
            {
                MaxDuration = 200.Milliseconds(), BatchSize = 10
            }, Token);
            passes++;

            var step = StepOf(report, StepOutgoing);
            calls += step.Calls;
            step.Result.OutgoingSent.ShouldBeLessThanOrEqualTo(step.Calls * 10);
            Log($"s4 drain pass {passes}: calls={step.Calls} sent={step.Result.OutgoingSent} remaining={step.Result.Remaining} timedOut={report.TimedOut} hasMoreWork={report.HasMoreWork}");
        } while (report.HasMoreWork && passes < 50);

        var sent = ControlledTransport.Behavior("slow").Sent;
        Log($"s4 drain: passes={passes} stepCalls={calls} distinctSent={sent.Count} duplicates={sent.Values.Count(x => x > 1)} outboxLeft={await CountOutgoingAsync()} maxPerSendBurst={ControlledTransport.Behavior("slow").MaxInFlight}");

        rows.All(x => sent.ContainsKey(x.Id)).ShouldBeTrue();
        sent.Values.ShouldAllBe(x => x == 1);
        (await CountOutgoingAsync()).ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Expiry cleanup
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task s5_expired_handled_rows_are_deleted_and_unexpired_ones_kept()
    {
        var settings = theHost.GetRuntime().Options.Durability;

        // What PushReceiver.recordHandledAsync writes under DeduplicateWithInbox: 1,050 already expired and
        // 40 that are not yet
        async Task storeHandledAsync(int count, DateTimeOffset now)
        {
            var handled = Enumerable.Range(0, count).Select(_ =>
            {
                var original = ObjectMother.Envelope();
                original.Destination = "ctl://push".ToUri();
                return Envelope.ForPersistedHandled(original, now, settings);
            }).ToArray();

            foreach (var chunk in handled.Chunk(100))
            {
                await StoreFor(theHost).Inbox.StoreIncomingAsync(chunk);
            }
        }

        await storeHandledAsync(1_050, DateTimeOffset.UtcNow.Subtract(1.Hours()));
        await storeHandledAsync(40, DateTimeOffset.UtcNow);

        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 1.Minutes(), BatchSize = 100
        }, Token);

        var step = StepOf(report, StepExpired);
        var left = await CountIncomingAsync(EnvelopeStatus.Handled);
        Log($"s5 handled cleanup: calls={step.Calls} deleted={step.Result.ExpiredDeleted} remaining={step.Result.Remaining} handledLeft={left}");

        step.Result.ExpiredDeleted.ShouldBe(1_050);
        left.ShouldBe(40);
    }

    [Fact]
    public async Task s5_an_inbox_row_recovered_by_a_pass_is_deleted_by_a_later_pass_after_KeepAfterMessageHandling()
    {
        var host = await BuildHostAsync(opts => opts.Durability.KeepAfterMessageHandling = 1.Seconds());
        var runtime = host.GetRuntime();

        var envelopes = Enumerable.Range(0, 30).Select(_ =>
        {
            var envelope = new Envelope(new SlowInboxMessage(Guid.NewGuid()))
            {
                Destination = "ctl://inbox".ToUri(),
                MessageType = typeof(SlowInboxMessage).ToMessageTypeName(),
                ContentType = EnvelopeConstants.JsonContentType,
                Status = EnvelopeStatus.Incoming,
                OwnerId = 0
            };
            envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
            return envelope;
        }).ToArray();

        SlowInboxHandler.Duration = TimeSpan.Zero;
        await StoreFor(host).Inbox.StoreIncomingAsync(envelopes);

        var first = await host.RunDurabilityPassAsync(30.Seconds(), Token);
        var handledAfterFirst = await CountIncomingAsync(EnvelopeStatus.Handled);
        await Task.Delay(1500.Milliseconds(), Token);
        var second = await host.RunDurabilityPassAsync(30.Seconds(), Token);
        var handledAfterSecond = await CountIncomingAsync(EnvelopeStatus.Handled);

        Log($"s5 recovered->handled->deleted: first recovered={first.IncomingRecovered} handledRows={handledAfterFirst}; second expiredDeleted={second.ExpiredDeleted} handledRows={handledAfterSecond}");

        first.IncomingRecovered.ShouldBe(30);
        handledAfterFirst.ShouldBe(30);
        second.ExpiredDeleted.ShouldBe(30);
        handledAfterSecond.ShouldBe(0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. Lock robustness
    // ---------------------------------------------------------------------------------------------------------

    private static DbConnection connectionOf(IAsyncDisposable passLock)
    {
        var field = passLock.GetType().GetField("_conn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (DbConnection)field.GetValue(passLock)!;
    }

    [Fact]
    public async Task s6_a_lock_held_by_a_hung_process_and_then_released_when_it_dies()
    {
        await StoreStaleOutgoingAsync("healthy", 5, 2.Hours());

        // A second "process" takes the lock and then hangs: never releases it, never closes its connection
        var crashed = await BuildHostAsync();
        var held = await StoreFor(crashed).As<IMessageStoreWithDurabilityPass>().TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        var skipped = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < 5.Seconds())
        {
            var report = await theHost.RunDurabilityPassAsync(10.Seconds(), Token);
            if (report.SkippedStores.Count == 1) skipped++;
            await Task.Delay(250, Token);
        }

        var hungSent = ControlledTransport.Behavior("healthy").Sent.Count;
        Log($"s6 hung holder: {skipped} pass(es) in 5 s all skipped, healthy sent={hungSent}");

        // Now the process dies: the server ends its session
        stopwatch.Restart();
        await KillSessionAsync(connectionOf(held));

        DurabilityPassReport? after = null;
        var attempts = 0;
        while (stopwatch.Elapsed < 30.Seconds())
        {
            attempts++;
            after = await theHost.RunDurabilityPassAsync(10.Seconds(), Token);
            if (after.SkippedStores.Count == 0) break;
            await Task.Delay(100, Token);
        }

        Log($"s6 after the holder's session died: lock acquired after {stopwatch.Elapsed.TotalMilliseconds:N0} ms and {attempts} attempt(s); {Describe(after!)}");

        // The abandoned lock object of the "dead" process
        try
        {
            await held.DisposeAsync();
        }
        catch
        {
            // its connection is gone
        }

        skipped.ShouldBeGreaterThan(0);
        hungSent.ShouldBe(0);
        after!.SkippedStores.ShouldBeEmpty();
        after.OutgoingSent.ShouldBe(5);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------------------------------------------

public record SlowInboxMessage(Guid Id);

public record RequeueingInboxMessage(Guid Id, bool Fail);

public class RequeueingInboxException : Exception;

public class RequeueingInboxHandler
{
    private static int _healthy;
    private static int _failingAttempts;

    public static int Healthy => Volatile.Read(ref _healthy);
    public static int FailingAttempts => Volatile.Read(ref _failingAttempts);

    public static void Reset()
    {
        _healthy = 0;
        _failingAttempts = 0;
    }

    public static void Handle(RequeueingInboxMessage message)
    {
        if (message.Fail)
        {
            Interlocked.Increment(ref _failingAttempts);
            throw new RequeueingInboxException();
        }

        Interlocked.Increment(ref _healthy);
    }
}

public class SlowInboxHandler
{
    private static int _started;
    private static int _completed;
    private static int _running;
    private static int _maxConcurrent;

    public static TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public static int Started => Volatile.Read(ref _started);
    public static int Completed => Volatile.Read(ref _completed);
    public static int Running => Volatile.Read(ref _running);
    public static int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    public static void Reset()
    {
        _started = 0;
        _completed = 0;
        _running = 0;
        _maxConcurrent = 0;
        Duration = TimeSpan.Zero;
    }

    public static async Task Handle(SlowInboxMessage message)
    {
        Interlocked.Increment(ref _started);
        var running = Interlocked.Increment(ref _running);
        int seen;
        while ((seen = Volatile.Read(ref _maxConcurrent)) < running &&
               Interlocked.CompareExchange(ref _maxConcurrent, running, seen) != seen)
        {
        }

        try
        {
            if (Duration > TimeSpan.Zero) await Task.Delay(Duration);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Interlocked.Increment(ref _completed);
        }
    }
}

public class ControlledBehavior
{
    private int _attempts;
    private int _inFlight;
    private int _maxInFlight;

    public bool Fail { get; set; }
    public bool Hang { get; set; }
    public TimeSpan Delay { get; set; }
    public ConcurrentDictionary<Guid, int> Sent { get; } = new();
    public int Attempts => Volatile.Read(ref _attempts);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    internal async ValueTask SendAsync(Envelope envelope)
    {
        Interlocked.Increment(ref _attempts);
        var inFlight = Interlocked.Increment(ref _inFlight);
        int seen;
        while ((seen = Volatile.Read(ref _maxInFlight)) < inFlight &&
               Interlocked.CompareExchange(ref _maxInFlight, inFlight, seen) != seen)
        {
        }

        try
        {
            if (Hang) await ControlledTransport.HangSignal.Task;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            if (Fail) throw new InvalidOperationException($"Controlled send to {envelope.Destination} failed");

            Sent.AddOrUpdate(envelope.Id, 1, (_, c) => c + 1);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// ctl://{name}: a sender whose behavior (fail, delay, hang) is set per name and shared by every host in the
/// process, and a listener the test can push envelopes into
/// </summary>
public class ControlledTransport : TransportBase<ControlledEndpoint>
{
    private static ConcurrentDictionary<string, ControlledBehavior> _behaviors = new();
    internal static TaskCompletionSource HangSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static ControlledBehavior Behavior(string name) => _behaviors.GetOrAdd(name, _ => new ControlledBehavior());

    public static void Reset()
    {
        ReleaseHangs();
        _behaviors = new ConcurrentDictionary<string, ControlledBehavior>();
        HangSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void ReleaseHangs() => HangSignal.TrySetResult();

    public ControlledTransport() : base("ctl", "Controlled test transport", [])
    {
        Endpoints = new LightweightCache<string, ControlledEndpoint>(name => new ControlledEndpoint(name));
    }

    public new LightweightCache<string, ControlledEndpoint> Endpoints { get; }

    public override bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    protected override IEnumerable<ControlledEndpoint> endpoints() => Endpoints;

    protected override ControlledEndpoint findEndpointByUri(Uri uri) => Endpoints[uri.Host];
}

public class ControlledEndpoint : Endpoint, ISender
{
    public ControlledEndpoint(string name) : base($"ctl://{name}".ToUri(), EndpointRole.Application)
    {
        EndpointName = name;
    }

    public ControlledListener? Listener { get; private set; }

    protected override bool supportsMode(EndpointMode mode)
    {
        // Senders inline, so a pass sends straight through; the listener can be durable
        return mode == EndpointMode.Inline || (IsListener && mode == EndpointMode.Durable);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        Listener = new ControlledListener(Uri, receiver);
        return ValueTask.FromResult<IListener>(Listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime) => this;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => Uri;
    public Task<bool> PingAsync() => Task.FromResult(true);

    public ValueTask SendAsync(Envelope envelope) => ControlledTransport.Behavior(EndpointName).SendAsync(envelope);
}

public class ControlledListener : IListener
{
    private readonly IReceiver _receiver;

    public ControlledListener(Uri address, IReceiver receiver)
    {
        Address = address;
        _receiver = receiver;
    }

    public ValueTask DeliverAsync(Envelope envelope) => _receiver.ReceivedAsync(this, envelope);

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    public Uri Address { get; }
    public ValueTask StopAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
