using System.Data.Common;
using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace OracleTests;

// GH-3581: with .UseDurableInbox() on an Oracle store, EfCoreEnvelopeTransaction.CommitAsync marked the
// already-persisted envelope handled by binding its Guid id through the generic Weasel path, which sets
// DbType.Guid. ODP.NET rejects that against a RAW(16) column with "Value does not fall within the expected
// range", rolling back the whole commit. The fix routes the update through
// IMessageDatabase.MarkIncomingEnvelopeAsHandledInTransactionAsync, which Oracle overrides to bind the Guid
// as byte[].
[Collection("oracle")]
public class mark_incoming_handled_in_transaction_binds_raw16_guid : IAsyncLifetime
{
    private OracleMessageStore theStore = null!;
    private OracleDataSource theDataSource = null!;

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
    {
        await theStore.DisposeAsync();
    }

    [Fact]
    public async Task marks_the_envelope_handled_inside_the_callers_transaction()
    {
        var envelope = ObjectMother.Envelope();
        await theStore.StoreIncomingAsync(envelope);

        var keepUntil = DateTimeOffset.UtcNow.AddHours(1);

        await using var conn = (OracleConnection)await theDataSource.OpenConnectionAsync();
        var tx = (DbTransaction)await conn.BeginTransactionAsync();

        // The exact call EfCoreEnvelopeTransaction.CommitAsync makes for a durable-inbox message,
        // sharing the caller's connection and transaction.
        await theStore.MarkIncomingEnvelopeAsHandledInTransactionAsync(conn, tx, envelope, keepUntil,
            CancellationToken.None);

        await tx.CommitAsync();
        await conn.CloseAsync();

        var counts = await theStore.Admin.FetchCountsAsync();
        counts.Handled.ShouldBeGreaterThanOrEqualTo(1);
        counts.Incoming.ShouldBe(0);
    }

    [Fact]
    public async Task the_generic_guid_binding_that_the_old_code_used_still_fails_on_raw16()
    {
        // Pins the root cause. Binding a Guid the generic way EfCoreEnvelopeTransaction used to — a
        // DbCommand typed parameter that resolves to DbType.Guid — is exactly what ODP.NET rejects
        // against a RAW(16) column. If this ever stops throwing, the provider-aware override is no
        // longer load-bearing and the fix can be simplified.
        var envelope = ObjectMother.Envelope();
        await theStore.StoreIncomingAsync(envelope);

        await using var conn = (OracleConnection)await theDataSource.OpenConnectionAsync();

        // conn typed as the base DbConnection, so .With(Guid) binds through the generic Weasel.Core
        // extension (DbType.Guid) rather than the Oracle-aware one — the pre-fix code path. ODP.NET
        // rejects DbType.Guid the instant the parameter Value is set, so the throw happens while the
        // command is being built (exactly the stack in the issue: OracleParameter.set_Value ←
        // CommandExtensions.With(Guid)), not at execution time.
        DbConnection baseConn = conn;
        Should.Throw<ArgumentException>(() =>
            baseConn.CreateCommand(
                    $"update WOLVERINE.{DatabaseConstants.IncomingTable} set {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' where id = @id")
                .With("id", envelope.Id));

        await conn.CloseAsync();
    }
}
