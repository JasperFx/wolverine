using JasperFx.Core;
using Shouldly;
using Wolverine.RDBMS;
using Xunit;

namespace Wolverine.ComplianceTests.Serverless;

/// <summary>
///     PR #5044 exploratory testing of DurabilityMode.ServerlessWithOutbox recovery. Each test runs real
///     "function instances" (the ServerlessOutboxCrashHost console app), freezes one at a chosen point with a TCP
///     destination that never answers, kills it with SIGKILL, and then has a fresh instance run a durability pass.
///     Delivery is counted by message id at a real Wolverine TCP listener.
/// </summary>
/// <remarks>
///     Build the crash host first: <c>dotnet build src/Testing/ServerlessOutboxCrashHost -c Release -f net9.0</c>.
///     Child output goes to <c>../explore5044-rec-logs/children</c> next to the repository.
/// </remarks>
public abstract class ServerlessOutboxRecoveryExploratory : IAsyncLifetime
{
    private static readonly TimeSpan Generous = 90.Seconds();

    private readonly List<IDisposable> _disposables = new();
    private readonly List<IAsyncDisposable> _asyncDisposables = new();

    /// <summary>
    ///     marten | postgresql | sqlserver | efcore, the crash host's store argument
    /// </summary>
    protected abstract string StoreName { get; }

    protected abstract string SchemaName { get; }

    /// <summary>
    ///     The table the crash host's transactional handler writes its business row to, in <see cref="SchemaName" />
    /// </summary>
    protected abstract string BusinessTable { get; }

    protected abstract Task<object?> ScalarAsync(string sql);

    public async ValueTask InitializeAsync()
    {
        await CrashHost.RunAsync($"{StoreName}-setup", [StoreName, SchemaName, "setup"]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _asyncDisposables) await disposable.DisposeAsync();
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    private T track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private async Task<PingReceiver> receiverAsync(int port)
    {
        var receiver = await PingReceiver.StartAsync(port);
        _asyncDisposables.Add(receiver);
        return receiver;
    }

    private async Task<long> countAsync(string sql)
    {
        return Convert.ToInt64(await ScalarAsync(sql));
    }

    protected Task<long> businessRowsAsync(Guid id)
    {
        return countAsync($"select count(*) from {SchemaName}.{BusinessTable} where id = '{id}'");
    }

    // Every test sends a single ExplorePing (scenario 3 counts the total instead), and the outbox row's id is the
    // envelope id, not the message id, so this counts the ExplorePing rows
    protected Task<long> outgoingRowsAsync(Guid id)
    {
        return countAsync(
            $"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable} where message_type = 'explore5044-ping'");
    }

    protected Task<long> outgoingTotalAsync()
    {
        return countAsync($"select count(*) from {SchemaName}.{DatabaseConstants.OutgoingTable}");
    }

    protected Task<long> incomingRowsAsync(string status)
    {
        return countAsync(
            $"select count(*) from {SchemaName}.{DatabaseConstants.IncomingTable} where status = '{status}'");
    }

    // The inbox row of the one ExplorePing a test sends (its envelope id is not the message id)
    protected async Task<(string? Status, int? Owner)> incomingPingRowAsync()
    {
        var where = $"from {SchemaName}.{DatabaseConstants.IncomingTable} where message_type = 'explore5044-ping'";
        var count = await countAsync($"select count(*) {where}");
        if (count == 0) return (null, null);
        count.ShouldBe(1);

        var status = await ScalarAsync($"select status {where}");
        var owner = await ScalarAsync($"select owner_id {where}");

        return (status as string, owner == null || owner is DBNull ? null : Convert.ToInt32(owner));
    }

    private static Dictionary<string, string> staleTimes(int? outboxMs = null, int? inboxMs = null)
    {
        var env = new Dictionary<string, string>();
        if (outboxMs.HasValue) env["EXPLORE_OUTBOX_STALE_MS"] = outboxMs.Value.ToString();
        if (inboxMs.HasValue) env["EXPLORE_INBOX_STALE_MS"] = inboxMs.Value.ToString();
        return env;
    }

    private async Task<Dictionary<string, int>> runPassAsync(string label, Dictionary<string, string>? env = null)
    {
        var pass = await CrashHost.RunAsync($"{StoreName}-{label}", [StoreName, SchemaName, "pass"], env);
        return CrashHost.ParsePass(await pass.WaitForMarkerAsync("@@PASS ", 5.Seconds()));
    }

    /// <summary>
    ///     A function instance commits its transaction and is frozen in the middle of the inline send (the broker
    ///     never answers), then killed. Leaves exactly one outbox row for <paramref name="id" /> pointed at
    ///     <paramref name="port" />
    /// </summary>
    private async Task freezeAndKillMidSendAsync(Guid id, int port, int count = 1)
    {
        using var blackhole = new Blackhole(port);

        using var function = CrashHost.Start($"{StoreName}-send-frozen",
            [StoreName, SchemaName, "send", port.ToString(), id.ToString(), "0", count.ToString()]);

        await function.WaitForMarkerAsync("@@INVOKING", Generous);
        await blackhole.FirstData.Task.WaitAsync(Generous);

        // Every send has reached the "broker", so every transaction has committed
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (blackhole.Connections < count && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        blackhole.Connections.ShouldBe(count, function.Output);

        await function.KillAsync();
        function.Markers.ShouldNotContain(x => x.StartsWith("@@DONE"));
    }

    [Fact]
    public async Task scenario_1_frozen_after_commit_before_send_is_recovered_once_by_the_next_instance()
    {
        var id = Guid.NewGuid();
        var port = CrashHost.FreePort();

        await freezeAndKillMidSendAsync(id, port);

        // The business data committed, and the outbox row survived the kill as owned by any node
        (await businessRowsAsync(id)).ShouldBe(1);
        (await outgoingRowsAsync(id)).ShouldBe(1);

        // The broker is back, and the next function instance runs a pass
        var receiver = await receiverAsync(port);

        // Within the default 60 second OutboxStaleTime the pass leaves it alone
        var early = await runPassAsync("pass-early");
        early["sent"].ShouldBe(0);

        await Task.Delay(1.Seconds());
        var report = await runPassAsync("pass", staleTimes(outboxMs: 500));
        report["sent"].ShouldBe(1);

        (await receiver.Counter.WaitForAsync(c => c.CountFor(id) >= 1, 15.Seconds())).ShouldBeTrue();
        (await outgoingRowsAsync(id)).ShouldBe(0);

        // And a later pass has nothing left to send
        var again = await runPassAsync("pass-again", staleTimes(outboxMs: 500));
        again["sent"].ShouldBe(0);

        await Task.Delay(2.Seconds());
        receiver.Counter.CountFor(id).ShouldBe(1);
    }

    [Fact]
    public async Task scenario_2_delivered_but_killed_before_the_ack_is_sent_again_by_the_pass()
    {
        var id = Guid.NewGuid();
        var receiver = await receiverAsync(CrashHost.FreePort());

        // The broker gets the message and handles it, but its acknowledgement never reaches the function
        var proxy = track(ReplyWithholdingProxy.Start("127.0.0.1", receiver.Port));
        proxy.WithholdReplyTo(_ => true);

        using (var function = CrashHost.Start($"{StoreName}-send-unacked",
                   [StoreName, SchemaName, "send", proxy.Port.ToString(), id.ToString()]))
        {
            (await receiver.Counter.WaitForAsync(c => c.CountFor(id) == 1, Generous)).ShouldBeTrue(function.Output);
            proxy.Tripped.ShouldBeTrue();

            // Killed before the inline send returned, so before the outbox row could be deleted
            await function.KillAsync();
            function.Markers.ShouldNotContain(x => x.StartsWith("@@DONE"));
        }

        (await businessRowsAsync(id)).ShouldBe(1);
        (await outgoingRowsAsync(id)).ShouldBe(1);

        proxy.Restore();

        await Task.Delay(1.Seconds());
        var report = await runPassAsync("pass", staleTimes(outboxMs: 500));
        report["sent"].ShouldBe(1);

        // At-least-once: the message is delivered a second time
        (await receiver.Counter.WaitForAsync(c => c.CountFor(id) == 2, 15.Seconds())).ShouldBeTrue();
        (await outgoingRowsAsync(id)).ShouldBe(0);

        await Task.Delay(2.Seconds());
        receiver.Counter.CountFor(id).ShouldBe(2);
    }

    [Fact]
    public async Task scenario_3_two_instances_running_a_pass_at_the_same_moment_send_each_row_once()
    {
        const int count = 40;
        var seed = Guid.NewGuid();
        var port = CrashHost.FreePort();

        await freezeAndKillMidSendAsync(seed, port, count);
        (await outgoingTotalAsync()).ShouldBe(count);

        // A slow broker in front of the receiver: every reply takes 3 seconds, so whichever pass gets the rows is
        // still inside its send when the other one starts
        var receiver = await receiverAsync(CrashHost.FreePort());
        var slow = track(new SlowReplyProxy(port, receiver.Port, 3.Seconds()));
        await Task.Delay(1.Seconds());

        // Both instances start their pass at the same instant, after both have finished starting up
        var at = DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds().ToString();
        var env = staleTimes(outboxMs: 500);
        env["EXPLORE_PASS_AT"] = at;

        using var first = CrashHost.Start($"{StoreName}-pass-a", [StoreName, SchemaName, "pass"], env);
        using var second = CrashHost.Start($"{StoreName}-pass-b", [StoreName, SchemaName, "pass"], env);

        var a = CrashHost.ParsePass(await first.WaitForMarkerAsync("@@PASS ", Generous));
        var b = CrashHost.ParsePass(await second.WaitForMarkerAsync("@@PASS ", Generous));

        (a["sent"] + b["sent"]).ShouldBe(count, $"a: {string.Join(", ", a)} / b: {string.Join(", ", b)}");

        // They really did overlap: one of them found the store locked by the other
        (a["skipped"] + b["skipped"]).ShouldBe(1, $"a: {string.Join(", ", a)} / b: {string.Join(", ", b)}");

        (await receiver.Counter.WaitForAsync(c => c.Total >= count, 15.Seconds())).ShouldBeTrue();
        await Task.Delay(2.Seconds());

        var ids = Enumerable.Range(0, count).Select(i => CrashHost.Derive(seed, i)).ToArray();
        var snapshot = receiver.Counter.Snapshot();
        ids.Where(x => snapshot.GetValueOrDefault(x) != 1)
            .Select(x => $"{x}={snapshot.GetValueOrDefault(x)}")
            .ShouldBeEmpty();
        (await outgoingTotalAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task scenario_3b_a_pass_killed_while_holding_the_lock_does_not_block_the_next_pass()
    {
        var id = Guid.NewGuid();
        var port = CrashHost.FreePort();

        await freezeAndKillMidSendAsync(id, port);
        (await outgoingRowsAsync(id)).ShouldBe(1);
        await Task.Delay(1.Seconds());

        // The pass that picks it up freezes in its own send, holding the store's pass lock, and is killed
        using (var blackhole = new Blackhole(port))
        using (var pass = CrashHost.Start($"{StoreName}-pass-frozen", [StoreName, SchemaName, "pass"],
                   staleTimes(outboxMs: 500)))
        {
            await pass.WaitForMarkerAsync("@@PASSING", Generous);
            await blackhole.FirstData.Task.WaitAsync(Generous);
            await pass.KillAsync();
        }

        (await outgoingRowsAsync(id)).ShouldBe(1);

        var receiver = await receiverAsync(port);
        var report = await runPassAsync("pass-after-kill", staleTimes(outboxMs: 500));

        report["skipped"].ShouldBe(0);
        report["sent"].ShouldBe(1);

        (await receiver.Counter.WaitForAsync(c => c.CountFor(id) == 1, 15.Seconds())).ShouldBeTrue();
        (await outgoingRowsAsync(id)).ShouldBe(0);
    }

    [Fact]
    public async Task scenario_4_a_scheduled_cascade_is_stored_and_released_once_when_due()
    {
        var id = Guid.NewGuid();
        var receiver = await receiverAsync(CrashHost.FreePort());

        var function = await CrashHost.RunAsync($"{StoreName}-send-scheduled",
            [StoreName, SchemaName, "send", receiver.Port.ToString(), id.ToString(), "4000"]);
        function.Markers.ShouldContain($"@@DONE {id}");

        (await businessRowsAsync(id)).ShouldBe(1);
        (await incomingRowsAsync(EnvelopeStatus.Scheduled.ToString())).ShouldBe(1, function.Output);
        (await outgoingTotalAsync()).ShouldBe(0);

        // Not due yet
        var early = await runPassAsync("pass-early");
        early["scheduled"].ShouldBe(0);
        receiver.Counter.CountFor(id).ShouldBe(0);

        await Task.Delay(4.Seconds());

        var due = await runPassAsync("pass-due");
        due["scheduled"].ShouldBe(1);
        (await receiver.Counter.WaitForAsync(c => c.CountFor(id) == 1, 15.Seconds())).ShouldBeTrue();

        var again = await runPassAsync("pass-again");
        again["scheduled"].ShouldBe(0);

        await Task.Delay(2.Seconds());
        receiver.Counter.CountFor(id).ShouldBe(1);
        (await incomingRowsAsync(EnvelopeStatus.Scheduled.ToString())).ShouldBe(0);
        (await outgoingTotalAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task scenario_5a_a_durable_listener_killed_mid_handler_is_recovered_after_the_inbox_stale_time()
    {
        var id = Guid.NewGuid();
        var port = CrashHost.FreePort();
        var sender = await receiverAsync(CrashHost.FreePort());

        using (var listener = CrashHost.Start($"{StoreName}-listen-killed",
                   [StoreName, SchemaName, "listen", port.ToString(), "true"]))
        {
            await listener.WaitForMarkerAsync("@@LISTENING", Generous);

            await sender.Host.MessageBus().EndpointFor(new Uri($"tcp://localhost:{port}"))
                .SendAsync(new ExploreReceivedPing(id));

            await listener.WaitForMarkerAsync($"@@HANDLING {id}", Generous);
            await listener.KillAsync();
        }

        var row = await incomingPingRowAsync();
        row.Status.ShouldBe(EnvelopeStatus.Incoming.ToString());
        row.Owner.ShouldNotBe(0);

        // Inside the default 5 minute InboxStaleTime the row is left to the process that claimed it
        var early = await runPassAsync("pass-early");
        early["incoming"].ShouldBe(0);

        await Task.Delay(1500.Milliseconds());
        var pass = await CrashHost.RunAsync($"{StoreName}-pass-inbox", [StoreName, SchemaName, "pass"],
            staleTimes(inboxMs: 1000));
        CrashHost.ParsePass(await pass.WaitForMarkerAsync("@@PASS ", 5.Seconds()))["incoming"].ShouldBe(1);
        pass.Markers.ShouldContain($"@@HANDLED {id}");

        var after = await incomingPingRowAsync();
        after.Status.ShouldNotBe(EnvelopeStatus.Incoming.ToString());

        var again = await runPassAsync("pass-inbox-again", staleTimes(inboxMs: 1000));
        again["incoming"].ShouldBe(0);
    }

    [Fact]
    public async Task scenario_5b_a_handler_slower_than_the_inbox_stale_time_is_run_again_by_a_pass()
    {
        var id = Guid.NewGuid();
        var port = CrashHost.FreePort();
        var sender = await receiverAsync(CrashHost.FreePort());

        using var listener = CrashHost.Start($"{StoreName}-listen-slow",
            [StoreName, SchemaName, "listen", port.ToString(), "true"]);
        await listener.WaitForMarkerAsync("@@LISTENING", Generous);

        await sender.Host.MessageBus().EndpointFor(new Uri($"tcp://localhost:{port}"))
            .SendAsync(new ExploreReceivedPing(id));
        await listener.WaitForMarkerAsync($"@@HANDLING {id}", Generous);

        // The listener is alive and still inside the handler
        await Task.Delay(1500.Milliseconds());
        var pass = await CrashHost.RunAsync($"{StoreName}-pass-slow", [StoreName, SchemaName, "pass"],
            staleTimes(inboxMs: 1000));

        listener.HasExited.ShouldBeFalse();
        CrashHost.ParsePass(await pass.WaitForMarkerAsync("@@PASS ", 5.Seconds()))["incoming"].ShouldBe(1);

        // A second copy ran while the first was still running
        pass.Markers.ShouldContain($"@@HANDLING {id}");

        await listener.KillAsync();
    }
}
