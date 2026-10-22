using System.Data.Common;
using Bobcat;
using Bobcat.Engine;
using DurableComplianceRig.Stores;
using Wolverine;

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
        SpecLog.Write($"{offlineRan} handler run(s) of {tenant}'s messages started while its database was offline");
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
