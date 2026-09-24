using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

/// <summary>
/// GH-4605, the Polecat mirror of <c>MartenTests/AncillaryStores/deduplication_rides_an_ancillary_marten_transaction</c>:
/// GH-4570's transactional claim, on the composition that actually threads an ancillary store marker
/// through the codegen — <c>AddPolecatStore&lt;T&gt;()</c> + <c>IntegrateWithWolverine()</c>, with the
/// handler routed there by <c>[PolecatStore]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Worth its own fixture because the claim has TWO ways to land in the wrong place here, and both are
/// silent. <c>PolecatDeduplicator.tableFor</c> resolves the claim table through
/// <c>IWolverineRuntime.Stores.FindAncillaryStore(marker)</c>; resolve the wrong store and the claim is
/// written to a different database's <c>wolverine_deduplication</c> table than the one the handler commits
/// to — so it survives a rollback of the work it is supposed to guard, and the id is poisoned. Or the
/// participant could be enlisted onto the wrong session. Nothing throws in either case.
/// </para>
/// <para>
/// Asserting against the ancillary schema's table BY NAME is what distinguishes those from a working
/// implementation; a behavioural assertion alone passes on all three.
/// </para>
/// </remarks>
public class deduplication_rides_an_ancillary_polecat_transaction : IAsyncLifetime
{
    private const string MainSchema = "anc_dedup_main";
    private const string StoreSchema = "anc_dedup_store";

    private readonly ITestOutputHelper _output;
    private IHost theHost = null!;

    public deduplication_rides_an_ancillary_polecat_transaction(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryPolecatDedupHandler))
                    .IncludeType(typeof(FailingAncillaryPolecatDedupHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertions below are counts rather than races.
                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = MainSchema;
                }).IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IAncillaryPolecatDedupStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = StoreSchema;
                    })
                    // SchemaName is spelled out where the Marten twin leaves it to the default, and it
                    // has to be. The two integrations do not agree on their fallback: the primary store's
                    // lands on store.Options.DatabaseSchemaName, but the ancillary one
                    // (AncillaryWolverineOptionsPolecatExtensions) falls through to the literal
                    // "wolverine". Both stores here are schemas in ONE SQL Server database, so taking the
                    // default would point both deduplication tables at the same object and make the
                    // wrong-store assertion below vacuously true.
                    .IntegrateWithWolverine(x => x.SchemaName = StoreSchema);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await theHost.ResetResourceState();

        AncillaryPolecatDedupHandler.Received.Clear();
        FailingAncillaryPolecatDedupHandler.Attempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_generated_claim_carries_the_ancillary_store_marker()
    {
        theHost.GetRuntime().Handlers.HandlerFor<AncillaryPolecatDedupMessage>();
        var chain = theHost.GetRuntime().Handlers.ChainFor<AncillaryPolecatDedupMessage>();
        chain.ShouldNotBeNull();
        var code = chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(code);

        // PolecatDeduplicationRendering.MarkerUsage renders the marker into both the claim check and the
        // enlistment. A null there is the silent failure this fixture exists for: it reads as "the main
        // store", and the claim goes to the wrong database's table.
        code.ShouldContain(nameof(IPolecatDeduplicator.QueueClaim));
        code.ShouldContain("typeof(PolecatTests.AncillaryStores.IAncillaryPolecatDedupStore)");

        // Enlisted on the session, so there is no compensating release to mis-order.
        code.ShouldNotContain("ReleaseAsync");
    }

    [Fact]
    public async Task the_claim_lands_in_the_ancillary_store_rather_than_the_main_one()
    {
        await theHost.SendMessageAndWaitAsync(new AncillaryPolecatDedupMessage("first"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        // The claim has to live beside the work it guards. In the main store it would commit on its own
        // and survive an ancillary rollback, which is the bug GH-4505 closed.
        (await claimCountAsync(StoreSchema, "anc-1")).ShouldBe(1);
        (await claimCountAsync(MainSchema, "anc-1")).ShouldBe(0);

        await theHost.SendMessageAndWaitAsync(new AncillaryPolecatDedupMessage("second"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        AncillaryPolecatDedupHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task a_rollback_in_the_ancillary_store_takes_the_claim_with_it()
    {
        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryPolecatDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryPolecatDedupHandler.Attempts.ShouldBe(1);
        (await claimCountAsync(StoreSchema, "anc-poison")).ShouldBe(0);

        // Both halves, or this passes vacuously: an empty ancillary table is also what a claim written to
        // the MAIN store looks like from here, and that claim would have committed on its own connection
        // and poisoned the id.
        (await claimCountAsync(MainSchema, "anc-poison")).ShouldBe(0);

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryPolecatDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryPolecatDedupHandler.Attempts.ShouldBe(2);
    }

    private static async Task<int> claimCountAsync(string schema, string key)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {schema}.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", key);

        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public interface IAncillaryPolecatDedupStore : IDocumentStore;

public record AncillaryPolecatDedupMessage(string Name);

public record FailingAncillaryPolecatDedupMessage;

public class AncillaryPolecatDedupRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[PolecatStore(typeof(IAncillaryPolecatDedupStore))]
public static class AncillaryPolecatDedupHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(AncillaryPolecatDedupMessage message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new AncillaryPolecatDedupRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

[PolecatStore(typeof(IAncillaryPolecatDedupStore))]
public static class FailingAncillaryPolecatDedupHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingAncillaryPolecatDedupMessage message, IDocumentSession session)
    {
        Attempts++;

        // Written first, so the test proves the ROLLBACK took the claim rather than proving the handler
        // never got far enough to enlist anything.
        session.Store(new AncillaryPolecatDedupRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}
