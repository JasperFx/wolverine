using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// GH-4375. The batched durability commands emit one values-clause per envelope, so parameter count
/// scaled with the batch. SQL Server refuses more than 2,100 parameters in one command, and the
/// transactional paths are NOT governed by any batch-size setting -- the array is however many
/// messages the handler published inside the caller's transaction.
///
/// <para>
/// Measured boundaries before the fix: outgoing succeeded at 349 envelopes (2,095 parameters) and
/// failed at 350 (2,101); incoming succeeded at 233 (2,097) and failed at 234 (2,106). The failure was
/// <c>SqlException: The incoming request has too many parameters</c> -- an error naming neither
/// Wolverine nor message counts.
/// </para>
///
/// <para>
/// These tests deliberately use sizes FAR past the old boundary rather than 350 and 234. Asserting on
/// the boundary alone would start passing again the moment the per-envelope parameter count changed,
/// which is exactly the kind of silent regression the chunking exists to prevent.
/// </para>
/// </summary>
[Collection("sqlserver")]
public class parameter_ceiling_4375 : IAsyncLifetime
{
    private const string TheSchema = "ceiling_4375";
    private SqlServerMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theStore = new SqlServerMessageStore(new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString, SchemaName = TheSchema
        }, new DurabilitySettings(), NullLogger<SqlServerMessageStore>.Instance,
            Array.Empty<SagaTableDefinition>());

        await theStore.Admin.MigrateAsync();
        await theStore.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync() => await theStore.DisposeAsync();

    private static Envelope[] envelopes(int count, EnvelopeStatus status)
    {
        return Enumerable.Range(0, count).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = status;
            return envelope;
        }).ToArray();
    }

    [Fact]
    public void sql_server_reports_the_tightest_ceiling_wolverine_ships()
    {
        theStore.MaximumParameterCount.ShouldBe(2100);
    }

    /// <summary>
    /// The chunk boundaries are computed from these constants, so a column added to either field list
    /// without updating them would silently move every boundary one envelope past what fits -- which is
    /// exactly the off-by-one this work hit, where the outgoing builder's shared owner_id parameter was
    /// left out of the budget and chunked to precisely the size already known to fail.
    /// </summary>
    [Fact]
    public void the_parameter_constants_match_what_the_builders_actually_add()
    {
        using var incoming = DatabasePersistence.BuildIncomingStorageCommand(
            envelopes(1, EnvelopeStatus.Incoming), theStore);
        incoming.Parameters.Count.ShouldBe(DatabaseConstants.IncomingParametersPerEnvelope);

        using var one = DatabasePersistence.BuildOutgoingStorageCommand(
            envelopes(1, EnvelopeStatus.Outgoing), 1, theStore);
        using var two = DatabasePersistence.BuildOutgoingStorageCommand(
            envelopes(2, EnvelopeStatus.Outgoing), 1, theStore);

        // The difference between one and two envelopes is the per-envelope cost; what is left over in
        // the single-envelope command is the shared part
        (two.Parameters.Count - one.Parameters.Count)
            .ShouldBe(DatabaseConstants.OutgoingParametersPerEnvelope);
        (one.Parameters.Count - DatabaseConstants.OutgoingParametersPerEnvelope)
            .ShouldBe(DatabaseConstants.OutgoingSharedParameters);
    }

    /// <summary>
    /// The arithmetic that broke: the largest batch that fits must actually fit. Both are one envelope
    /// under the measured failure points (350 outgoing, 234 incoming).
    /// </summary>
    [Fact]
    public async Task the_largest_unchunked_batch_still_fits()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await theStore.StoreOutgoingAsync(tx, envelopes(349, EnvelopeStatus.Outgoing));
        await theStore.StoreIncomingAsync(tx, envelopes(233, EnvelopeStatus.Incoming));

        await tx.CommitAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 1,000 outgoing envelopes is 6,001 parameters unchunked -- nearly 3x the ceiling.
    /// </summary>
    [Fact]
    public async Task a_transaction_can_publish_far_more_messages_than_the_parameter_ceiling_allows()
    {
        var outgoing = envelopes(1000, EnvelopeStatus.Outgoing);

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await theStore.StoreOutgoingAsync(tx, outgoing);
        await tx.CommitAsync(TestContext.Current.CancellationToken);

        var stored = await theStore.Admin.AllOutgoingAsync();
        stored.Count.ShouldBe(outgoing.Length);
        outgoing.ShouldAllBe(x => x.WasPersistedInOutbox);
    }

    /// <summary>
    /// 1,000 incoming envelopes is 9,000 parameters unchunked -- over 4x the ceiling.
    /// </summary>
    [Fact]
    public async Task a_transaction_can_receive_far_more_messages_than_the_parameter_ceiling_allows()
    {
        var incoming = envelopes(1000, EnvelopeStatus.Incoming);

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await theStore.StoreIncomingAsync(tx, incoming);
        await tx.CommitAsync(TestContext.Current.CancellationToken);

        var counts = await theStore.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(incoming.Length);
    }

    /// <summary>
    /// Chunking must not cost the all-or-nothing guarantee. The caller owns the transaction, so
    /// rolling it back has to leave nothing behind even though the write spanned several commands.
    /// </summary>
    [Fact]
    public async Task a_rolled_back_transaction_leaves_nothing_behind_across_chunks()
    {
        var outgoing = envelopes(1000, EnvelopeStatus.Outgoing);

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using (var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await theStore.StoreOutgoingAsync(tx, outgoing);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await theStore.Admin.AllOutgoingAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// The connection-owning batch path is bounded by StoreOutgoingBatchSize today, but nothing stops
    /// an application raising that setting past the ceiling.
    /// </summary>
    [Fact]
    public async Task the_batched_outgoing_store_survives_a_batch_past_the_ceiling()
    {
        var outgoing = envelopes(1000, EnvelopeStatus.Outgoing);

        await theStore.Outbox.StoreOutgoingAsync(outgoing, 5890);

        (await theStore.Admin.AllOutgoingAsync()).Count.ShouldBe(outgoing.Length);
    }

    [Fact]
    public async Task the_batched_incoming_store_survives_a_batch_past_the_ceiling()
    {
        var incoming = envelopes(1000, EnvelopeStatus.Incoming);

        await theStore.Inbox.StoreIncomingAsync(incoming);

        var counts = await theStore.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(incoming.Length);
    }
}
