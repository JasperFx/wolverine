using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.Sqlite;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Scheduled;
using Wolverine.Sqlite;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;
using Wolverine.Util;

namespace SqliteTests;

/// <summary>
/// GH-5028. Under DurabilityMode.ServerlessWithOutbox there is no DurabilityAgent and no scheduled job polling,
/// so the durability pass is what releases due scheduled messages, recovers incoming rows and runs the expiry
/// cleanups.
/// </summary>
[Collection("sqlite")]
public class durability_pass_steps_5028 : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(durability_pass_steps_5028));

    private IHost theHost = null!;
    private SqliteMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        DurabilityPassMessageHandler.Handled.Clear();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DurabilityPassMessageHandler));
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage.As<SqliteMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        _database.Dispose();
    }

    private IReadOnlyList<Envelope> sentTo(string queueName)
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>().Endpoints[queueName].Sent
            .ToArray();
    }

    private Envelope localEnvelope(EnvelopeStatus status, string destination = "stub://inbox")
    {
        var message = new DurabilityPassMessage(Guid.NewGuid());
        var envelope = new Envelope(message)
        {
            Destination = destination.ToUri(),
            MessageType = typeof(DurabilityPassMessage).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType,
            Status = status,
            OwnerId = 0
        };

        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);
        return envelope;
    }

    private async Task<Envelope> storeDueScheduledSendAsync(string destination)
    {
        var inner = ObjectMother.Envelope();
        inner.Destination = destination.ToUri();
        inner.DeliverBy = null;
        inner.ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Minutes());

        var wrapped = inner.ForScheduledSend(null);
        await theStore.Inbox.StoreIncomingAsync(wrapped);

        return inner;
    }

    private async Task<Dictionary<Guid, string>> inboxStatusesAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, status from {DatabaseConstants.IncomingTable}";

        var rows = new Dictionary<Guid, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        }

        return rows;
    }

    // The expiry columns are written from a bound DateTimeOffset, so they are backdated the same way
    private async Task setExpiryAsync(string table, string column, DateTimeOffset value, Guid? id = null)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = id.HasValue
            ? $"update {table} set {column} = @value where lower(id) = '{id.Value:D}'"
            : $"update {table} set {column} = @value";
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task one_pass_releases_due_scheduled_messages_and_recovers_incoming_rows()
    {
        // A scheduled send to an external endpoint
        var external = await storeDueScheduledSendAsync("stub://scheduled");

        // A local message that is due, and one that is not
        var dueLocal = localEnvelope(EnvelopeStatus.Scheduled);
        dueLocal.ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Minutes());
        await theStore.Inbox.StoreIncomingAsync(dueLocal);

        var notDue = localEnvelope(EnvelopeStatus.Scheduled);
        notDue.ScheduledTime = DateTimeOffset.UtcNow.Add(1.Hours());
        await theStore.Inbox.StoreIncomingAsync(notDue);

        // An incoming row persisted but never handled, and one still owned by a node
        var recoverable = localEnvelope(EnvelopeStatus.Incoming);
        await theStore.Inbox.StoreIncomingAsync(recoverable);

        var owned = localEnvelope(EnvelopeStatus.Incoming);
        owned.OwnerId = 5;
        await theStore.Inbox.StoreIncomingAsync(owned);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.FailedSteps.ShouldBe(0);
        report.ScheduledReleased.ShouldBe(2);
        report.IncomingRecovered.ShouldBe(1);
        report.Remaining.ShouldBe(0);

        sentTo("scheduled").Single().Id.ShouldBe(external.Id);

        DurabilityPassMessageHandler.Handled.OrderBy(x => x)
            .ShouldBe(new[] { dueLocal.Message!.As<DurabilityPassMessage>().Id, recoverable.Message!.As<DurabilityPassMessage>().Id }.OrderBy(x => x));

        var rows = await inboxStatusesAsync();
        rows[dueLocal.Id].ShouldBe(EnvelopeStatus.Handled.ToString());
        rows[recoverable.Id].ShouldBe(EnvelopeStatus.Handled.ToString());
        rows[notDue.Id].ShouldBe(EnvelopeStatus.Scheduled.ToString());
        rows[owned.Id].ShouldBe(EnvelopeStatus.Incoming.ToString());

        // Nothing is left for a second pass
        var second = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        second.ScheduledReleased.ShouldBe(0);
        second.IncomingRecovered.ShouldBe(0);
        sentTo("scheduled").Count.ShouldBe(1);
    }

    [Fact]
    public async Task due_scheduled_messages_are_released_in_batches()
    {
        for (var i = 0; i < 5; i++)
        {
            await storeDueScheduledSendAsync("stub://batched-scheduled");
        }

        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 30.Seconds(),
            BatchSize = 2
        }, Token);

        report.ScheduledReleased.ShouldBe(5);
        report.HasMoreWork.ShouldBeFalse();

        var step = report.Stores.Single(x => x.StoreUri == theStore.Uri).Steps
            .Single(x => x.StepName == "Release due scheduled messages");
        step.Calls.ShouldBe(3);

        sentTo("batched-scheduled").Count.ShouldBe(5);
    }

    [Fact]
    public async Task a_pass_deletes_expired_handled_envelopes_and_dead_letters()
    {
        var expired = localEnvelope(EnvelopeStatus.Handled);
        await theStore.Inbox.StoreIncomingAsync(expired);

        var retained = localEnvelope(EnvelopeStatus.Handled);
        await theStore.Inbox.StoreIncomingAsync(retained);

        await setExpiryAsync(DatabaseConstants.IncomingTable, DatabaseConstants.KeepUntil,
            DateTimeOffset.UtcNow.Subtract(1.Hours()), expired.Id);
        await setExpiryAsync(DatabaseConstants.IncomingTable, DatabaseConstants.KeepUntil,
            DateTimeOffset.UtcNow.Add(1.Hours()), retained.Id);

        var deadLetter = localEnvelope(EnvelopeStatus.Incoming);
        await theStore.Inbox.StoreIncomingAsync(deadLetter);
        await theStore.Inbox.MoveToDeadLetterStorageAsync(deadLetter, new DivideByZeroException());
        await setExpiryAsync(DatabaseConstants.DeadLetterTable, DatabaseConstants.Expires,
            DateTimeOffset.UtcNow.Subtract(1.Hours()));

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.FailedSteps.ShouldBe(0);
        report.ExpiredDeleted.ShouldBe(2);

        (await inboxStatusesAsync()).Keys.ShouldBe([retained.Id]);

        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {DatabaseConstants.DeadLetterTable}";
        Convert.ToInt64(await cmd.ExecuteScalarAsync(Token)).ShouldBe(0);
    }
}

public record DurabilityPassMessage(Guid Id);

// Kept out of conventional discovery, where it would give the other tests' hosts an extra handler. The
// durability pass host includes it explicitly
[WolverineIgnore]
public static class DurabilityPassMessageHandler
{
    public static readonly List<Guid> Handled = new();

    public static void Handle(DurabilityPassMessage message)
    {
        lock (Handled)
        {
            Handled.Add(message.Id);
        }
    }
}
