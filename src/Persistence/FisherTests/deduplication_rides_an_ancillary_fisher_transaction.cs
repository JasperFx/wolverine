using Fisher;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
/// GH-4605, the Fisher mirror of <c>MartenTests/AncillaryStores/deduplication_rides_an_ancillary_marten_transaction</c>:
/// GH-4571's transactional claim, on the composition that actually threads an ancillary store marker
/// through the codegen — <c>AddFisherStore&lt;T&gt;()</c> + <c>IntegrateWithWolverine()</c>, with the
/// handler routed there by <c>[FisherStore]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Worth its own fixture because the claim has TWO ways to land in the wrong place here, and both are
/// silent. <c>FisherDeduplicator.tableFor</c> resolves the claim table through
/// <c>IWolverineRuntime.Stores.FindAncillaryStore(marker)</c>; resolve the wrong store and the claim is
/// written to a different database's <c>wolverine_deduplication</c> table than the one the handler commits
/// to — so it survives a rollback of the work it is supposed to guard, and the id is poisoned. Or the
/// participant could be enlisted onto the wrong session. Nothing throws in either case.
/// </para>
/// <para>
/// A Fisher store is a SQLite <b>file</b>, so "the wrong table" here is a table in the wrong file, and the
/// negative assertion is a count against the main database. That also keeps the two stores from becoming
/// two writers on one SQLite file.
/// </para>
/// </remarks>
public class deduplication_rides_an_ancillary_fisher_transaction : IAsyncLifetime
{
    private const string AncillarySchema = "anc_dedup_store";

    private readonly ITestOutputHelper _output;
    private FisherTestDatabase theAncillaryDatabase = null!;
    private FisherTestDatabase theMainDatabase = null!;
    private IHost theHost = null!;

    public deduplication_rides_an_ancillary_fisher_transaction(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        theMainDatabase = Servers.CreateDatabase("anc_dedup_main");
        theAncillaryDatabase = Servers.CreateDatabase("anc_dedup_store");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryFisherDedupHandler))
                    .IncludeType(typeof(FailingAncillaryFisherDedupHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertions below are counts rather than races.
                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMainDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IAncillaryFisherDedupStore>(m =>
                    {
                        m.Connection(theAncillaryDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    // SchemaName is spelled out, and it is load-bearing rather than cosmetic. Both
                    // integrations default to the literal "main" (SQLite has no user-defined schemas), and
                    // Fisher sets SchemaNameIsTablePrefix -- so on the defaults BOTH stores name their
                    // deduplication table "main_wolverine_deduplication". The claim is enlisted as an
                    // ITransactionParticipant on the session, which is the ancillary one either way, so a
                    // deduplicator that resolved the MAIN store for the table name would still write to the
                    // right file under the right name, and every assertion below would pass over a broken
                    // marker. Giving the ancillary store its own prefix is what makes the wrong table a
                    // different table.
                    .IntegrateWithWolverine(x => x.SchemaName = AncillarySchema);
            }).StartAsync(TestContext.Current.CancellationToken);

        AncillaryFisherDedupHandler.Received.Clear();
        FailingAncillaryFisherDedupHandler.Attempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theAncillaryDatabase.Dispose();
        theMainDatabase.Dispose();
    }

    [Fact]
    public void the_generated_claim_carries_the_ancillary_store_marker()
    {
        theHost.GetRuntime().Handlers.HandlerFor<AncillaryFisherDedupMessage>();
        var chain = theHost.GetRuntime().Handlers.ChainFor<AncillaryFisherDedupMessage>();
        chain.ShouldNotBeNull();
        var code = chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(code);

        // FisherDeduplicationRendering.MarkerUsage renders the marker into both the claim check and the
        // enlistment. A null there is the silent failure this fixture exists for: it reads as "the main
        // store", and the claim goes to the wrong file's table.
        code.ShouldContain(nameof(IFisherDeduplicator.QueueClaim));
        code.ShouldContain("typeof(FisherTests.IAncillaryFisherDedupStore)");

        // Enlisted on the session, so there is no compensating release to mis-order.
        code.ShouldNotContain("ReleaseAsync");
    }

    [Fact]
    public async Task the_claim_lands_in_the_ancillary_store_rather_than_the_main_one()
    {
        await theHost.SendMessageAndWaitAsync(new AncillaryFisherDedupMessage("first"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        // The claim has to live beside the work it guards. In the main store it would commit on its own
        // and survive an ancillary rollback, which is the bug GH-4505 closed.
        (await claimCountAsync(theAncillaryDatabase, "anc-1")).ShouldBe(1);
        (await claimCountAsync(theMainDatabase, "anc-1")).ShouldBe(0);

        await theHost.SendMessageAndWaitAsync(new AncillaryFisherDedupMessage("second"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        AncillaryFisherDedupHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task a_rollback_in_the_ancillary_store_takes_the_claim_with_it()
    {
        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryFisherDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryFisherDedupHandler.Attempts.ShouldBe(1);
        (await claimCountAsync(theAncillaryDatabase, "anc-poison")).ShouldBe(0);

        // Both halves, or this passes vacuously: an empty ancillary table is also what a claim written to
        // the MAIN store looks like from here, and that claim would have committed on its own connection
        // and poisoned the id.
        (await claimCountAsync(theMainDatabase, "anc-poison")).ShouldBe(0);

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryFisherDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryFisherDedupHandler.Attempts.ShouldBe(2);
    }

    private static async Task<int> claimCountAsync(FisherTestDatabase database, string key)
    {
        await using var conn = new SqliteConnection(database.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var table = await deduplicationTableAsync(conn);

        // The main store provisions a deduplication table of its own, so its absence would be a setup
        // failure rather than a passing negative. The ancillary assertion is only worth something if the
        // main table exists and is empty.
        table.ShouldNotBeNull("the deduplication table was never provisioned in this database");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {table} where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", key);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string?> deduplicationTableAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();

        // Fisher sets SchemaNameIsTablePrefix, so the deduplication table is ONE prefixed identifier
        // rather than schema.table, and the prefix follows the store's schema name. Looked up rather than
        // spelled out, so this asserts against whatever the integration actually provisioned.
        cmd.CommandText =
            "select name from sqlite_master where type = 'table' and name like '%wolverine_deduplication'";

        return (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}

public interface IAncillaryFisherDedupStore : IDocumentStore;

public record AncillaryFisherDedupMessage(string Name);

public record FailingAncillaryFisherDedupMessage;

public class AncillaryFisherDedupRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[FisherStore(typeof(IAncillaryFisherDedupStore))]
public static class AncillaryFisherDedupHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(AncillaryFisherDedupMessage message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new AncillaryFisherDedupRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

[FisherStore(typeof(IAncillaryFisherDedupStore))]
public static class FailingAncillaryFisherDedupHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingAncillaryFisherDedupMessage message, IDocumentSession session)
    {
        Attempts++;

        // Written first, so the test proves the ROLLBACK took the claim rather than proving the handler
        // never got far enough to enlist anything.
        session.Store(new AncillaryFisherDedupRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}
