using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
/// GH-4505 on an ancillary store, which is the composition the GH-4501 reporter is actually running:
/// <c>AddMartenStore&lt;T&gt;()</c> + <c>IntegrateWithWolverine()</c>, with the handler routed there by
/// <c>[MartenStore]</c>.
/// </summary>
/// <remarks>
/// Worth its own fixture because the claim has TWO ways to land in the wrong place here, and both are
/// silent. It could be written to the main store's deduplication table — in which case it commits while
/// the ancillary transaction rolls back, and the id is poisoned exactly as before — or it could be queued
/// onto the wrong session. Asserting against the ancillary schema's table by name is what distinguishes
/// those from a working implementation; a behavioural assertion alone passes on all three.
/// </remarks>
public class deduplication_rides_an_ancillary_marten_transaction : IAsyncLifetime
{
    private const string MainSchema = "anc_dedup_main";
    private const string StoreSchema = "anc_dedup_store";

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryDedupHandler))
                    .IncludeType(typeof(FailingAncillaryDedupHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = MainSchema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IAncillaryDedupStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = StoreSchema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await theHost.ResetResourceState();

        AncillaryDedupHandler.Received.Clear();
        FailingAncillaryDedupHandler.Attempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_claim_lands_in_the_ancillary_store_rather_than_the_main_one()
    {
        await theHost.SendMessageAndWaitAsync(new AncillaryDedupMessage("first"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        // The claim has to live beside the work it guards. In the main store it would commit on its own
        // and survive an ancillary rollback, which is the bug this issue closes.
        (await claimCountAsync(StoreSchema, "anc-1")).ShouldBe(1);
        (await claimCountAsync(MainSchema, "anc-1")).ShouldBe(0);

        await theHost.SendMessageAndWaitAsync(new AncillaryDedupMessage("second"),
            new DeliveryOptions { DeduplicationId = "anc-1" });

        AncillaryDedupHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task a_rollback_in_the_ancillary_store_takes_the_claim_with_it()
    {
        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryDedupHandler.Attempts.ShouldBe(1);
        (await claimCountAsync(StoreSchema, "anc-poison")).ShouldBe(0);

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingAncillaryDedupMessage(),
                new DeliveryOptions { DeduplicationId = "anc-poison" });

        FailingAncillaryDedupHandler.Attempts.ShouldBe(2);
    }

    private static async Task<long> claimCountAsync(string schema, string key)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {schema}.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", key);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public interface IAncillaryDedupStore : IDocumentStore;

public record AncillaryDedupMessage(string Name);

public record FailingAncillaryDedupMessage;

public class AncillaryDedupRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[MartenStore(typeof(IAncillaryDedupStore))]
public static class AncillaryDedupHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(AncillaryDedupMessage message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new AncillaryDedupRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

[MartenStore(typeof(IAncillaryDedupStore))]
public static class FailingAncillaryDedupHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingAncillaryDedupMessage message, IDocumentSession session)
    {
        Attempts++;
        session.Store(new AncillaryDedupRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}
