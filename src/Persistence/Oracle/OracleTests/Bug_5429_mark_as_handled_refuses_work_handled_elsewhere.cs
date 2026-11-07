using System.Data.Common;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace OracleTests;

/// <summary>
/// GH-5429. Oracle implements the in-transaction mark-as-handled itself (GH-3581, RAW(16) ids), so it missed the
/// GH-5423 fence: a node ejected as stale that resumed and finished envelopes whose rows had been recovered and
/// handled elsewhere committed that work a second time. The mark has to leave a Handled row alone and refuse the
/// commit when another execution already finished the message.
/// </summary>
[Collection("oracle")]
public class Bug_5429_mark_as_handled_refuses_work_handled_elsewhere : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private OracleMessageStore theStore = null!;
    private OracleDataSource theDataSource = null!;

    public async ValueTask InitializeAsync()
    {
        theDataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "WOLVERINE",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };

        theStore = new OracleMessageStore(settings, new DurabilitySettings(), theDataSource,
            NullLogger<OracleMessageStore>.Instance);

        await theStore.Admin.MigrateAsync();
        await theStore.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theStore.DisposeAsync();
    }

    private async Task<Envelope> storeIncomingAsync()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = new Uri("local://gh5429");
        await theStore.StoreIncomingAsync(envelope);
        return envelope;
    }

    private async Task markInTransactionAsync(Envelope envelope, DateTimeOffset keepUntil)
    {
        await using var conn = (OracleConnection)await theDataSource.OpenConnectionAsync(Token);
        var tx = (DbTransaction)await conn.BeginTransactionAsync(Token);

        await theStore.MarkIncomingEnvelopeAsHandledInTransactionAsync(conn, tx, envelope, keepUntil, Token);

        await tx.CommitAsync(Token);
    }

    private async Task<Envelope?> rowOfAsync(Envelope envelope)
    {
        return (await theStore.Admin.AllIncomingAsync()).SingleOrDefault(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task an_incoming_row_is_marked_handled()
    {
        var envelope = await storeIncomingAsync();

        await markInTransactionAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(5));

        (await rowOfAsync(envelope))!.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task a_row_another_execution_already_handled_refuses_the_commit()
    {
        var envelope = await storeIncomingAsync();

        // The node that recovered the row while this one was frozen handled it first
        var other = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.KeepUntil = other;
        await theStore.MarkIncomingEnvelopeAsHandledAsync(envelope);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
            markInTransactionAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(10)));

        // ...and the other execution's Handled row is left as it was
        var row = await rowOfAsync(envelope);
        row!.Status.ShouldBe(EnvelopeStatus.Handled);
        row.KeepUntil!.Value.ShouldBe(other, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task a_missing_row_does_not_refuse_the_commit()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = new Uri("local://gh5429");

        await markInTransactionAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task an_eager_idempotency_row_written_by_this_execution_does_not_refuse_the_commit()
    {
        var envelope = await storeIncomingAsync();
        await theStore.MarkIncomingEnvelopeAsHandledAsync(envelope);

        // Eager idempotency flips the in-memory status when it writes its own Handled row
        envelope.Status = EnvelopeStatus.Handled;

        await markInTransactionAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
