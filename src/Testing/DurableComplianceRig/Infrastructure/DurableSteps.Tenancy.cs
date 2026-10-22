using System.Data.Common;
using Bobcat;
using Bobcat.Engine;
using DurableComplianceRig.Stores;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// Steps for one message store per tenant: messages minted per tenant and sent with its tenant id, a tenant database
/// taken offline and brought back, and the routing checks -- every effect, inbox row and dead letter in the
/// database its tenant owns.
/// </summary>
public sealed partial class DurableSteps
{
    private readonly Dictionary<Guid, string?> _tenantOf = [];
    private readonly HashSet<string> _offline = [];
    private readonly List<Guid> _refused = [];

    private TenancyStoreProfile tenancyStore => Store as TenancyStoreProfile
        ?? throw new SpecCriticalException($"{Store} has no database per tenant");

    public string? TenantOf(Guid id) => _tenantOf.GetValueOrDefault(id);

    private string databaseOf(Guid id) => tenancyStore.LabelFor(TenantOf(id));

    [Given("{count} messages to send for each tenant")]
    public void MessagesToSendForEachTenant(int count)
    {
        foreach (var tenant in tenancyStore.Tenants) MessagesToSendForTenant(count, tenant);
    }

    [Given("{count} messages to send for tenant {tenant}")]
    public void MessagesToSendForTenant(int count, string tenant)
    {
        var first = _messages.Count + 1;
        MessagesToSend(count);
        foreach (var id in _messages.Skip(first - 1)) _tenantOf[id] = tenant;
        SpecLog.Write($"messages {first}..{_messages.Count} belong to tenant {tenant}");
    }

    /// <summary>Every message carries its tenant id; one without a tenant goes out untenanted, to the main database.</summary>
    [When("the messages are sent from node {node} with their tenant ids")]
    public async Task TheMessagesAreSentWithTheirTenants(int node)
    {
        var bus = Node(node).MessageBus();
        var refused = new List<string>();
        foreach (var id in _messages)
        {
            var tenant = TenantOf(id);
            try
            {
                await bus.PublishAsync(new RigMessage(id), new DeliveryOptions { TenantId = tenant });
            }
            catch (Exception e)
            {
                // The sender saw the failure, so the message is the caller's to retry, not the inbox's to lose
                refused.Add($"{id} ({tenant}): {e.GetType().Name}: {e.Message}");
                _refused.Add(id);
                _expected.Remove(id);
            }
        }

        if (refused.Count > 0)
        {
            SpecLog.Write($"{refused.Count} send(s) refused to the caller: {refused.Join("; ")}");
            _output.WriteLine($"refused sends: {refused.Join(Environment.NewLine)}");
        }
    }

    [Then("every effect, inbox row and dead letter is in its tenant's database")]
    public async Task EverythingIsInItsTenantsDatabase()
    {
        var invariants = new Invariants(await Store.SnapshotAsync(), _expected);
        foreach (var row in invariants.Snapshot.Ledger.OrderBy(x => x.RecordedAt)) _output.WriteLine($"ledger: {row}");
        while (TenantRdbmsLedger.UnscriptedFailures.TryDequeue(out var failure)) _output.WriteLine($"unscripted failure: {failure}");

        SpecAssert.Check("effects committed in another tenant's database",
            describe(invariants.MisroutedEffects(tenant => tenancyStore.LabelFor(tenant))
                .Select(x => $"{x.MessageId} of {x.Tenant ?? "no tenant"} in {x.Database}")), "none");

        // The ledger records the tenant the handler saw: a message handled under another tenant's id
        SpecAssert.Check("effects whose handler saw another tenant id than the message was sent with",
            describe(invariants.Snapshot.Ledger.Where(x => _expected.ContainsKey(x.MessageId) && x.Tenant != TenantOf(x.MessageId))
                .Select(x => $"{x.MessageId} sent as {TenantOf(x.MessageId) ?? "no tenant"}, handled as {x.Tenant ?? "no tenant"}")),
            "none");

        SpecAssert.Check("inbox rows in another tenant's database",
            describe(invariants.MisroutedInboxRows(databaseOf).Select(x => $"{x.Id} of {TenantOf(x.Id)} {x.Status} in {x.Database}")),
            "none");

        SpecAssert.Check("dead letters in another tenant's database",
            describe(invariants.MisroutedDeadLetters(databaseOf).Select(x => $"{x.Id} of {TenantOf(x.Id)} in {x.Database}")),
            "none");

        // Guards the two checks above: every durable receipt leaves an inbox row (Handled rows are kept for a while)
        // or, once dead lettered, a dead letter row, so a run with neither would pass them without judging anything
        var withRows = _expected.Keys.Count(id =>
            invariants.Snapshot.Inbox.Any(x => x.Id == id && x.Database == databaseOf(id))
            || invariants.Snapshot.DeadLetters.Any(x => x.Id == id && x.Database == databaseOf(id)));
        SpecAssert.Check("messages with an inbox or dead letter row in their tenant's database", withRows, _expected.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // A tenant database going away

    [When("tenant {tenant}'s database goes offline")]
    public async Task TenantDatabaseGoesOffline(string tenant)
    {
        _offline.Add(tenant);
        await tenancyStore.Tenancy.TakeOfflineAsync(tenant);
        SpecLog.Write($"{tenancyStore.Tenancy.NameFor(tenant)} is offline");
    }

    [When("tenant {tenant}'s database comes back online")]
    public async Task TenantDatabaseComesBack(string tenant)
    {
        await tenancyStore.Tenancy.BringOnlineAsync(tenant);
        _offline.Remove(tenant);
        SpecLog.Write($"{tenancyStore.Tenancy.NameFor(tenant)} is back online");
    }

    [Then("the sends of {count} messages for tenant {tenant} were refused to the caller")]
    public void TheSendsForTenantWereRefused(string tenant, int count)
    {
        SpecAssert.Check($"sends for {tenant} refused to the caller", _refused.Count(id => TenantOf(id) == tenant), count);
        SpecAssert.Check("sends refused for any other tenant",
            describe(_refused.Where(id => TenantOf(id) != tenant).Select(id => $"{id} ({TenantOf(id)})")), "none");
    }

    /// <summary>
    /// Sends from a host with no message store at all, straight to the broker, so the tenant's messages reach the
    /// listener whatever state the tenant's database is in. RabbitMQ only: a local queue has no outside.
    /// </summary>
    [When("the messages are sent with their tenant ids from a sender with no message store")]
    public async Task TheMessagesAreSentFromABareSender()
    {
        if (Transport is not RabbitMqTransport)
        {
            throw new SpecCriticalException($"A sender outside the cluster needs a broker, not {Transport}");
        }

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "durable-rig-sender";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
                opts.PublishMessage<RigMessage>().ToRabbitQueue(QueueName).CustomizeOutgoing(RigIds.Stamp);
            }).StartAsync();

        var bus = sender.MessageBus();
        foreach (var id in _messages)
        {
            await bus.PublishAsync(new RigMessage(id), new DeliveryOptions { TenantId = TenantOf(id) });
        }

        await sender.StopAsync();
    }

    /// <summary>Not a step: puts back any database a failed scenario left offline, so the next one can start.</summary>
    public async Task RestoreOfflineTenantsAsync()
    {
        foreach (var tenant in _offline.ToArray())
        {
            try
            {
                await tenancyStore.Tenancy.BringOnlineAsync(tenant);
            }
            catch (Exception e)
            {
                _output.WriteLine($"Could not bring {tenant} back online: {e.Message}");
            }
        }

        _offline.Clear();
    }

    /// <summary>
    /// GH-4435/4658: one tenant's database being down must not stop the listener for the others. Judged while it is
    /// still down, reading only the databases that are up.
    /// </summary>
    [Then("every other tenant's messages are handled while tenant {tenant}'s database is offline")]
    public async Task OtherTenantsAreHandledWhileOffline(string tenant)
    {
        var others = _expected.Keys.Where(id => TenantOf(id) != tenant).ToArray();
        var reachable = Store.Databases.Where(x => x.Label != tenant).ToArray();

        IReadOnlyList<Guid> waiting = others;
        await pollAsync(async () =>
        {
            var snapshot = new RigSnapshot { Executions = Script.Log };
            foreach (var database in reachable) await database.ReadIntoAsync(snapshot);
            waiting = others.Where(id => snapshot.Ledger.All(x => x.MessageId != id)
                                         && snapshot.DeadLetters.All(x => x.Id != id)).ToArray();
            return waiting.Count == 0;
        }, SettleTimeout);

        SpecAssert.Check($"other tenants' messages not handled while {tenant} was offline",
            describe(waiting.Select(id => $"{id} ({TenantOf(id)})")), "none");

        var offlineRan = Script.Log.Count(x => TenantOf(x.MessageId) == tenant);
        var stranded = _expected.Keys.Count(id => TenantOf(id) == tenant);
        var line = $"{stranded} of {tenant}'s messages accepted for delivery while its database was offline; " +
                   $"{offlineRan} handler run(s) of them started before it came back";
        SpecLog.Write(line);
        _output.WriteLine(line);
    }

    /// <summary>
    /// A disposed host's handler is still parked in this process; a real crash would have killed it. Its runs are
    /// finished here, so the checker does not read them as still running alongside the recovered run.
    /// </summary>
    [When("the runs node {node} had in flight died with it")]
    public void TheRunsInFlightDiedWithTheNode(int node)
    {
        foreach (var execution in Script.Log.Where(x => x.Node == $"node {node}" && x.FinishedAt == null))
        {
            execution.Finish("died with the node");
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Error policies per tenant

    [Then("message {number} waits for its retry as a scheduled row in its tenant's database")]
    public async Task MessageIsScheduledInItsTenantsDatabase(int number)
    {
        var id = MessageNumbered(number);
        InboxRow? scheduled = null;
        await pollAsync(async () =>
        {
            scheduled = (await Store.SnapshotAsync()).Inbox.FirstOrDefault(x => x.Id == id && x.Status == "Scheduled");
            return scheduled != null;
        }, 30.Seconds());

        SpecAssert.Check($"the database holding message {number}'s scheduled retry", scheduled?.Database ?? "no scheduled row seen",
            databaseOf(id));
    }

    // ---------------------------------------------------------------------------------------------------------
    // F19: the inbox heartbeat per tenant

    /// <summary>
    /// Not a step. The F19 decisive experiment (<c>RIG_TENANT_HEARTBEAT=1</c>): the rig itself restarts the stale
    /// clock of the run's owned Incoming rows in every database, three times per stale time, exactly as
    /// <c>MessageDatabase.RestartStaleClockAsync</c> would if the receiver called it. If the duplicate then goes away,
    /// the missing heartbeat is the whole mechanism.
    /// </summary>
    public void RestartStaleClocksForTheReceiverIfAsked(TimeSpan staleTime)
    {
        if (Environment.GetEnvironmentVariable("RIG_TENANT_HEARTBEAT") != "1") return;

        SpecLog.Write("RIG_TENANT_HEARTBEAT=1: the rig restarts the stale clock of the run's rows itself");
        var ids = _messages.ToArray();
        var period = TimeSpan.FromTicks(staleTime.Ticks / 3);
        _ = Task.Run(async () =>
        {
            while (!_rigHeartbeatStop.IsCancellationRequested)
            {
                foreach (var database in Store.Databases)
                {
                    try
                    {
                        await restartStaleClocksAsync(database, ids);
                    }
                    catch (Exception e)
                    {
                        _output.WriteLine($"rig heartbeat on {database.Label}: {e.Message}");
                    }
                }

                try
                {
                    await Task.Delay(period, _rigHeartbeatStop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private readonly CancellationTokenSource _rigHeartbeatStop = new();

    private static async Task restartStaleClocksAsync(RigDatabase database, Guid[] ids)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var now = database.Dialect == Dialect.Postgresql ? "now()" : "GETUTCDATE()";
        cmd.CommandText =
            $"update {database.Schema}.wolverine_incoming_envelopes set timestamp = {now} " +
            $"where owner_id <> 0 and status = 'Incoming' and id in ({ids.Select((_, i) => "@p" + i).Join(", ")})";
        for (var i = 0; i < ids.Length; i++)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "p" + i;
            parameter.Value = ids[i];
            cmd.Parameters.Add(parameter);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Not a step: stops the F19 experiment's heartbeat, if it ran.</summary>
    public async Task StopRigHeartbeatAsync()
    {
        // The loop notices within one period; nothing waits on it
        await _rigHeartbeatStop.CancelAsync();
    }
}

public sealed partial class DurableSteps
{
    /// <summary>
    /// Not a step: a diagnostic. Writes every change of a message's inbox row (status, owner, attempts) to the test
    /// output for <paramref name="duration" />, in the background, so a row that ends in the wrong state can be traced.
    /// </summary>
    public void TraceInboxRow(int number, TimeSpan duration)
    {
        var id = MessageNumbered(number);
        var database = Store.Databases.Single(x => x.Label == databaseOfOrMain(id));
        _ = Task.Run(async () =>
        {
            string? last = null;
            var deadline = DateTimeOffset.UtcNow.Add(duration);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var snapshot = new RigSnapshot();
                    await database.ReadIntoAsync(snapshot);
                    var row = snapshot.Inbox.FirstOrDefault(x => x.Id == id);
                    var state = row == null ? "no row" : $"{row.Status} owner={row.OwnerId} attempts={row.Attempts}";
                    if (state != last) _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} message {number} row: {state}");
                    last = state;
                }
                catch (Exception e)
                {
                    _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} message {number} row: {e.GetType().Name}");
                }

                await Task.Delay(50);
            }
        });
    }

    private string databaseOfOrMain(Guid id) => Store is TenancyStoreProfile ? databaseOf(id) : Store.Databases[0].Label;
}

public sealed partial class DurableSteps
{
    /// <summary>
    /// Not a step: a diagnostic for SQL Server (<c>RIG_TRACE_BLOCKING=1</c>). Writes every blocked request on the
    /// server -- who blocks whom, and the statements on both sides -- to the test output for <paramref name="duration" />.
    /// </summary>
    public void TraceSqlServerBlockingIfAsked(TimeSpan duration)
    {
        if (Environment.GetEnvironmentVariable("RIG_TRACE_BLOCKING") != "1" || Store.Databases[0].Dialect != Dialect.SqlServer) return;

        var database = Store.Databases[0];
        _ = Task.Run(async () =>
        {
            var seen = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.Add(duration);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await using var conn = database.Connect();
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        select r.session_id, r.blocking_session_id, db_name(r.database_id), r.wait_type, t.text,
                               (select top 1 bt.text from sys.dm_exec_connections c
                                  cross apply sys.dm_exec_sql_text(c.most_recent_sql_handle) bt
                                  where c.session_id = r.blocking_session_id)
                        from sys.dm_exec_requests r cross apply sys.dm_exec_sql_text(r.sql_handle) t
                        where r.blocking_session_id <> 0
                        """;
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var line = $"session {reader.GetValue(0)} in {reader.GetValue(2)} blocked by {reader.GetValue(1)} ({reader.GetValue(3)}): " +
                                   $"{reader.GetValue(4)} || blocker last ran: {reader.GetValue(5)}";
                        if (seen.Add(line)) _output.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} BLOCKED {line}");
                    }
                }
                catch (Exception e)
                {
                    _output.WriteLine($"blocking trace: {e.Message}");
                }

                await Task.Delay(250);
            }
        });
    }
}
