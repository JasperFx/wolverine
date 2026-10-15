using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Scheduled;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;
using Wolverine.Util;

namespace SqlServerTests;

/// <summary>
/// GH-5028. Under DurabilityMode.ServerlessWithOutbox there is no DurabilityAgent and no scheduled job polling,
/// so the durability pass is what releases due scheduled messages, recovers incoming rows and runs the expiry
/// cleanups.
/// </summary>
public class durability_pass_steps_5028 : IAsyncLifetime
{
    private const string SchemaName = "durability_pass_steps";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;
    private SqlServerMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        DurabilityPassMessageHandler.Handled.Clear();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                // The rows these tests store as received were received at this listener's address. A due scheduled row
                // parked under an address with no listener is a scheduled send the pass forwards to that endpoint
                opts.ListenForMessagesFrom("stub://inbox");
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DurabilityPassMessageHandler));
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage.As<SqlServerMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
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
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var rows = new Dictionary<Guid, string>();
        await using var reader = await conn
            .CreateCommand($"select id, status from {SchemaName}.{DatabaseConstants.IncomingTable}")
            .ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows[await reader.GetFieldValueAsync<Guid>(0)] = await reader.GetFieldValueAsync<string>(1);
        }

        return rows;
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(sql).ExecuteNonQueryAsync();
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

        await executeAsync(
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.KeepUntil} = dateadd(hour, -1, sysdatetimeoffset()) where id = '{expired.Id}'");
        await executeAsync(
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.KeepUntil} = dateadd(hour, 1, sysdatetimeoffset()) where id = '{retained.Id}'");

        var deadLetter = localEnvelope(EnvelopeStatus.Incoming);
        await theStore.Inbox.StoreIncomingAsync(deadLetter);
        await theStore.Inbox.MoveToDeadLetterStorageAsync(deadLetter, new DivideByZeroException());
        await executeAsync(
            $"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Expires} = dateadd(hour, -1, sysdatetimeoffset())");

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.FailedSteps.ShouldBe(0);
        report.ExpiredDeleted.ShouldBe(2);

        (await inboxStatusesAsync()).Keys.ShouldBe([retained.Id]);

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(Token);
        var deadLetters = await conn
            .CreateCommand($"select count(*) from {SchemaName}.{DatabaseConstants.DeadLetterTable}")
            .ExecuteScalarAsync(Token);
        Convert.ToInt64(deadLetters).ShouldBe(0);
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
