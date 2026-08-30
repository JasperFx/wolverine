using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Persistence;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// GH-4180. End-to-end coverage for logical message deduplication against a real PostgreSQL store.
///
/// <para>
/// These deliberately drive whole messages through the bus rather than
/// calling <c>IDeduplicationStore</c> directly. The store is the easy half; what needs proving is
/// that the generated handler resolves the id, claims it, and returns early — a store-level test
/// would pass over a chain that never wove the frames in at all.
/// </para>
/// </summary>
public class logical_message_deduplication : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dedup");
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.Policies.AutoApplyTransactions();

                // GH-4180 follow up: the id is derived from the message type rather than supplied by
                // the publisher at each call site
                opts.MessageDeduplication
                    .ByMessage<ComposedIdentityMessage>(x => $"{x.Tenant}|{x.Sequence}");

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertion below is a count rather than a race.
                opts.OnException<DivideByZeroException>().Discard();
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        DeduplicatedHandler.Received.Clear();
        UnkeyedHandler.Received.Clear();
        DerivedIdentityHandler.Received.Clear();
        FailingDeduplicatedHandler.Attempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task provisions_the_deduplication_table()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var table = await new Table(new DbObjectName("dedup", DatabaseConstants.DeduplicationTableName))
            .FetchExistingAsync(conn, TestContext.Current.CancellationToken);

        table.ShouldNotBeNull();
        table.HasColumn(DatabaseConstants.DeduplicationId).ShouldBeTrue();
        table.HasColumn(DatabaseConstants.Expires).ShouldBeTrue();
    }

    [Fact]
    public async Task second_message_with_the_same_logical_id_is_discarded()
    {
        await theHost.SendMessageAndWaitAsync(new DeduplicatedMessage("first"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });

        // Same logical id, DIFFERENT payload and a different Envelope.Id -- so nothing but the logical
        // id can be doing the work here. Envelope.Id idempotency would let this straight through.
        await theHost.SendMessageAndWaitAsync(new DeduplicatedMessage("second"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });

        DeduplicatedHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        await theHost.SendMessageAndWaitAsync(new DeduplicatedMessage("a"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });

        await theHost.SendMessageAndWaitAsync(new DeduplicatedMessage("b"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-30T03:00:00Z" });

        DeduplicatedHandler.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_message_with_no_logical_id_is_unaffected_when_the_id_is_optional()
    {
        // Twice, with no deduplication id at all. An unkeyed stream on a Required = false chain must
        // behave exactly as if the feature were off -- including paying no database round trip, which
        // is why the generated claim is guarded by a null check rather than called unconditionally.
        await theHost.SendMessageAndWaitAsync(new UnkeyedMessage("x"));
        await theHost.SendMessageAndWaitAsync(new UnkeyedMessage("y"));

        UnkeyedHandler.Received.ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task a_missing_but_required_logical_id_throws_rather_than_discarding()
    {
        // The opposite of a duplicate: nothing has been done and nothing will be, so this must be loud.
        var session = await theHost
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new DeduplicatedMessage("no id"));

        session.AllExceptions().OfType<MissingDeduplicationIdException>().ShouldNotBeEmpty();
        DeduplicatedHandler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_failed_execution_does_not_poison_the_logical_id()
    {
        // The single most damaging way to get this wrong: a handler that throws leaves its claim behind,
        // every retry is refused as a duplicate of its own failed attempt, and the work is silently
        // never done while the logs report successful deduplication.
        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingDeduplicatedMessage(),
                new DeliveryOptions { DeduplicationId = "poison-check" });

        FailingDeduplicatedHandler.Attempts.ShouldBe(1);

        // Second attempt at the SAME logical id must reach the handler again, because the compensating
        // release removed the claim the failed attempt took.
        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingDeduplicatedMessage(),
                new DeliveryOptions { DeduplicationId = "poison-check" });

        FailingDeduplicatedHandler.Attempts.ShouldBe(2);
    }

    // GH-4180 follow up. The publishing-side derivation is only worth anything if the id it stamps is
    // the same id the receiving handler enforces -- these two halves are wired up in completely
    // different places, and a routing-level test alone would pass over a handler that never sees it.
    [Fact]
    public async Task an_id_derived_from_a_marked_member_is_enforced()
    {
        await theHost.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-17", "first"));

        // Same identity member, different payload, different Envelope.Id
        await theHost.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-17", "second"));

        await theHost.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-18", "third"));

        DerivedIdentityHandler.Received.ShouldBe(["first", "third"]);
    }

    [Fact]
    public async Task an_id_derived_from_a_configured_lambda_is_enforced()
    {
        await theHost.SendMessageAndWaitAsync(new ComposedIdentityMessage("acme", 1, "first"));
        await theHost.SendMessageAndWaitAsync(new ComposedIdentityMessage("acme", 1, "second"));

        // Only the tenant differs, so this is a different logical message
        await theHost.SendMessageAndWaitAsync(new ComposedIdentityMessage("globex", 1, "third"));

        DerivedIdentityHandler.Received.ShouldBe(["first", "third"]);
    }

    [Fact]
    public async Task an_explicit_delivery_option_still_wins_over_the_derived_id()
    {
        await theHost.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-19", "first"),
            new DeliveryOptions { DeduplicationId = "override" });

        // A DIFFERENT identity member, but the same explicit override -- so if the derived id were
        // winning, both of these would run
        await theHost.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-20", "second"),
            new DeliveryOptions { DeduplicationId = "override" });

        DerivedIdentityHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task the_reaper_deletes_expired_claims_and_reports_how_many()
    {
        var store = theHost.GetRuntime().Storage.Deduplication;

        await store.TryClaimAsync("expired-1", DateTimeOffset.UtcNow.Subtract(1.Hours()), TestContext.Current.CancellationToken);
        await store.TryClaimAsync("expired-2", DateTimeOffset.UtcNow.Subtract(1.Hours()), TestContext.Current.CancellationToken);
        await store.TryClaimAsync("still-live", DateTimeOffset.UtcNow.Add(1.Hours()), TestContext.Current.CancellationToken);

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        deleted.ShouldBe(2);

        // The live claim survives, and is still refused
        (await store.TryClaimAsync("still-live", DateTimeOffset.UtcNow.Add(1.Hours()), TestContext.Current.CancellationToken)).ShouldBeFalse();

        // The expired ones are claimable again
        (await store.TryClaimAsync("expired-1", DateTimeOffset.UtcNow.Add(1.Hours()), TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task concurrent_claims_of_the_same_id_produce_exactly_one_winner()
    {
        // The race this feature exists for. A SELECT-then-INSERT implementation passes every other test
        // in this file and fails only here.
        var store = theHost.GetRuntime().Storage.Deduplication;
        var expires = DateTimeOffset.UtcNow.Add(1.Hours());

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => store.TryClaimAsync("contended", expires, TestContext.Current.CancellationToken)));

        results.Count(x => x).ShouldBe(1);
    }
}

public record DeduplicatedMessage(string Name);

public record MarkedIdentityMessage([property: DeduplicationIdentity] string InvoiceNumber, string Name);

public record ComposedIdentityMessage(string Tenant, int Sequence, string Name);

public record UnkeyedMessage(string Name);

public record FailingDeduplicatedMessage;

public static class DeduplicatedHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public static void Handle(DeduplicatedMessage message)
    {
        Received.Add(message.Name);
    }
}

public static class DerivedIdentityHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    public static void Handle(MarkedIdentityMessage message)
    {
        Received.Add(message.Name);
    }

    [Deduplicated]
    public static void Handle(ComposedIdentityMessage message)
    {
        Received.Add(message.Name);
    }
}

public static class UnkeyedHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated(Required = false)]
    public static void Handle(UnkeyedMessage message)
    {
        Received.Add(message.Name);
    }
}

public static class FailingDeduplicatedHandler
{
    public static int Attempts;

    [Deduplicated]
    public static void Handle(FailingDeduplicatedMessage message)
    {
        Attempts++;
        throw new DivideByZeroException("nope");
    }
}
