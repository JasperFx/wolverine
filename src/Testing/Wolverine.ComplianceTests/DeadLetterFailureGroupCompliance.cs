using FailureGroupSamples;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5209. Failure fingerprints, grouped summaries, operation counts, replay history, archive / restore and
/// exception message matching, against every store that implements them. Each test checks
/// <see cref="IDeadLetters.Capabilities"/> first, so a store that does not offer a feature skips it.
/// </summary>
public abstract class DeadLetterFailureGroupCompliance : IAsyncLifetime
{
    protected const string ServiceName = "Service1";

    protected IDeadLetters theDeadLetters = null!;
    protected EnvelopeGenerator theGenerator = null!;
    protected IMessageStore thePersistence = null!;

    public IHost theHost { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await BuildCleanHost();

        await theHost.ResetResourceState();

        thePersistence = theHost.Services.GetRequiredService<IMessageStore>();
        theDeadLetters = thePersistence.DeadLetters;

        theGenerator = new EnvelopeGenerator
        {
            MessageSource = () => new TargetMessage1(Guid.NewGuid(), 1, "Red"),
            StartingTime = DateTimeOffset.UtcNow.AddHours(-1)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    public abstract Task<IHost> BuildCleanHost();

    protected bool supports(DeadLetterCapabilities capability) =>
        (theDeadLetters.Capabilities & capability) == capability;

    protected async Task<Envelope> deadLetter(Exception exception, Action<Envelope>? configure = null)
    {
        var envelope = theGenerator.BuildEnvelope();

        // keep the expiration column predictable for the stores that enable dead letter expiration
        envelope.DeliverBy = null;
        configure?.Invoke(envelope);

        await thePersistence.Inbox.StoreIncomingAsync(envelope);
        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);

        return envelope;
    }

    protected async Task deadLetters(int count, Func<int, Exception> exception, Action<Envelope>? configure = null)
    {
        for (var i = 0; i < count; i++)
        {
            await deadLetter(exception(i), configure);
        }
    }

    protected async Task<List<DeadLetterEnvelope>> all(DeadLetterEnvelopeQuery? query = null)
    {
        query ??= new DeadLetterEnvelopeQuery();
        query.PageSize = 0;
        var results = await theDeadLetters.QueryAsync(query, CancellationToken.None);
        return results.Envelopes;
    }

    protected async Task<string> groupOf(Exception exception)
    {
        var envelope = await deadLetter(exception);
        var stored = await theDeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery([envelope.Id]), CancellationToken.None);
        return stored!.FailureGroup!;
    }

    /// <summary>
    /// Simulate dead letters recorded before failure fingerprinting existed
    /// </summary>
    protected virtual async Task clearFailureGroupsAsync()
    {
        var database = (IMessageDatabase)thePersistence;
        var builder = database.ToCommandBuilder();
        builder.Append(
            $"update {database.TableNameFor(DatabaseConstants.DeadLetterTable)} set {DatabaseConstants.FailureGroup} = null, {DatabaseConstants.FailureTitle} = null");

        await using var conn = await database.DataSource.OpenConnectionAsync();
        await using var cmd = builder.Compile();
        cmd.Connection = conn;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// What the durability agent does on every recovery pass: move replayable dead letters to the inbox
    /// </summary>
    protected virtual Task moveReplayableDeadLettersAsync()
    {
        var database = (IMessageDatabase)thePersistence;
        var batch = new DatabaseOperationBatch(database,
            [new MoveReplayableErrorMessagesToIncomingOperation(database)]);
        return theHost.InvokeAsync(batch);
    }

    protected virtual Task expireDeadLettersAsync(DurabilitySettings settings, DateTimeOffset now)
    {
        var database = (IMessageDatabase)thePersistence;
        var batch = new DatabaseOperationBatch(database,
            [new DeleteExpiredDeadLetterMessagesOperation(database, NullLogger.Instance, now, settings)]);
        return theHost.InvokeAsync(batch);
    }

    [Fact]
    public async Task fingerprints_are_persisted_and_stable_across_exception_messages()
    {
        if (!supports(DeadLetterCapabilities.FailureGroups)) return;

        await deadLetters(3, i => Failures.CardDeclined($"card {i} for customer {Guid.NewGuid()}"));
        await deadLetters(2, i => Failures.OutOfStock($"sku {i}"));

        var stored = await all();
        stored.Count.ShouldBe(5);
        stored.ShouldAllBe(x => x.FailureGroup != null && x.FailedAt != null);

        var cards = stored.Where(x => x.ExceptionMessage.StartsWith("Card")).ToList();
        var stock = stored.Where(x => x.ExceptionMessage.StartsWith("Nothing")).ToList();

        // Same bug, different data in the message: one group
        cards.Select(x => x.FailureGroup).Distinct().Count().ShouldBe(1);
        stock.Select(x => x.FailureGroup).Distinct().Count().ShouldBe(1);

        // Same exception type, different bug: different groups
        cards[0].FailureGroup.ShouldNotBe(stock[0].FailureGroup);

        cards[0].FailureTitle.ShouldBe("System.InvalidOperationException: PaymentProcessor.ChargeCard(...)");
        stock[0].FailureTitle.ShouldBe("System.InvalidOperationException: Shipping.ShipOrder(...)");

        // The group also travels inside the stored envelope
        cards[0].Envelope.Headers[DeadLetterQueueConstants.FailureGroupHeader].ShouldBe(cards[0].FailureGroup);

        // ...and the same failure always produces the same key
        (await groupOf(Failures.CardDeclined("something else entirely"))).ShouldBe(cards[0].FailureGroup);
    }

    [Fact]
    public async Task summarize_by_failure_group()
    {
        if (!supports(DeadLetterCapabilities.FailureGroups)) return;

        await deadLetters(4, i => Failures.CardDeclined(i.ToString()));
        await deadLetters(2, i => Failures.OutOfStock(i.ToString()));

        var summaries = await theDeadLetters.SummarizeAllAsync(ServiceName, new DeadLetterSummaryOptions(),
            CancellationToken.None);

        summaries.Count.ShouldBe(2);

        var cards = summaries.Single(x => x.Count == 4);
        cards.Grouping.ShouldBe(DeadLetterGrouping.FailureGroup);
        cards.Title.ShouldBe("System.InvalidOperationException: PaymentProcessor.ChargeCard(...)");
        cards.ExceptionType.ShouldBe("System.InvalidOperationException");
        cards.PredatesFingerprinting.ShouldBeFalse();
        cards.FirstFailedAt.ShouldNotBeNull();
        cards.LastFailedAt.ShouldNotBeNull();
        cards.LastFailedAt!.Value.ShouldBeGreaterThanOrEqualTo(cards.FirstFailedAt!.Value);

        // The key selects the group again
        var group = await all(new DeadLetterEnvelopeQuery { FailureGroup = cards.Key });
        group.Count.ShouldBe(4);
        group.ShouldAllBe(x => x.ExceptionMessage.StartsWith("Card"));
    }

    [Fact]
    public async Task summarize_by_message_type_and_by_endpoint()
    {
        if (!supports(DeadLetterCapabilities.FailureGroups)) return;

        await deadLetters(3, i => Failures.CardDeclined(i.ToString()));

        theGenerator.MessageSource = () => new TargetMessage2(Guid.NewGuid(), 2, "Blue");
        await deadLetters(2, i => Failures.CardDeclined(i.ToString()), e => e.Destination = new Uri("local://two"));

        var byType = await theDeadLetters.SummarizeAllAsync(ServiceName,
            new DeadLetterSummaryOptions { Grouping = DeadLetterGrouping.MessageType }, CancellationToken.None);

        byType.Count.ShouldBe(2);
        byType.Single(x => x.Key == typeof(TargetMessage1).ToMessageTypeName()).Count.ShouldBe(3);
        byType.Single(x => x.Key == typeof(TargetMessage2).ToMessageTypeName()).Count.ShouldBe(2);

        var byEndpoint = await theDeadLetters.SummarizeAllAsync(ServiceName,
            new DeadLetterSummaryOptions { Grouping = DeadLetterGrouping.Endpoint }, CancellationToken.None);

        byEndpoint.Count.ShouldBe(2);
        byEndpoint.Single(x => x.Key == new Uri("local://two").ToString()).Count.ShouldBe(2);
        byEndpoint.ShouldAllBe(x => x.FirstFailedAt != null && x.LastFailedAt != null);

        // The summary query filters like any other query
        var oneGroup = await theDeadLetters.SummarizeAllAsync(ServiceName,
            new DeadLetterSummaryOptions
            {
                Grouping = DeadLetterGrouping.MessageType,
                Query = new DeadLetterEnvelopeQuery { ReceivedAt = new Uri("local://two").ToString() }
            }, CancellationToken.None);

        oneGroup.Single().Count.ShouldBe(2);
    }

    [Fact]
    public async Task failure_group_filters_query_replay_and_discard_with_counts()
    {
        if (!supports(DeadLetterCapabilities.FailureGroups | DeadLetterCapabilities.OperationCounts)) return;

        await deadLetters(5, i => Failures.CardDeclined(i.ToString()));
        await deadLetters(3, i => Failures.OutOfStock(i.ToString()));

        var cards = (await all()).First(x => x.ExceptionMessage.StartsWith("Card")).FailureGroup!;
        var stock = (await all()).First(x => x.ExceptionMessage.StartsWith("Nothing")).FailureGroup!;

        (await all(new DeadLetterEnvelopeQuery { FailureGroup = cards })).Count.ShouldBe(5);

        var replayed = await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = cards },
            CancellationToken.None);
        replayed.Affected.ShouldBe(5);

        (await all(new DeadLetterEnvelopeQuery { Replayable = true })).ShouldAllBe(x => x.FailureGroup == cards);

        var discarded = await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = stock },
            CancellationToken.None);
        discarded.Affected.ShouldBe(3);

        (await all()).Count.ShouldBe(5);

        // Nothing matched, nothing touched
        var none = await theDeadLetters.DiscardMatchingAsync(
            new DeadLetterEnvelopeQuery { FailureGroup = "no-such-group" }, CancellationToken.None);
        none.Affected.ShouldBe(0);
        (await all()).Count.ShouldBe(5);
    }

    [Fact]
    public async Task counts_through_the_header_filter_path_sum_every_chunk()
    {
        if (!supports(DeadLetterCapabilities.OperationCounts)) return;

        // More than one 500 id chunk of matches, plus rows that must not be touched
        theGenerator.Headers["batch"] = "big";
        await deadLetters(520, i => Failures.CardDeclined(i.ToString()));
        theGenerator.Headers.Clear();
        await deadLetters(4, i => Failures.CardDeclined(i.ToString()));

        var replayed = await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery().Header("batch", "big"),
            CancellationToken.None);
        replayed.Affected.ShouldBe(520);

        var discarded = await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery().Header("batch", "big"),
            CancellationToken.None);
        discarded.Affected.ShouldBe(520);

        (await all()).Count.ShouldBe(4);

        var none = await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery().Header("batch", "nope"),
            CancellationToken.None);
        none.Affected.ShouldBe(0);
        (await all()).Count.ShouldBe(4);
    }

    [Fact]
    public async Task exception_message_contains_versus_exact()
    {
        if (!supports(DeadLetterCapabilities.ExceptionMessageMatching)) return;

        await deadLetter(new InvalidOperationException("Order 100% failed_badly"));
        await deadLetter(new InvalidOperationException("Order 100X failedXbadly"));
        await deadLetter(new InvalidOperationException("Order 200 was fine"));

        var exact = await all(new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "Order 100% failed_badly", ExceptionMessageMatch = DeadLetterMessageMatch.Exact
        });
        exact.Single().ExceptionMessage.ShouldBe("Order 100% failed_badly");

        // Wildcard characters in the value are matched literally
        var contains = await all(new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "100% failed_", ExceptionMessageMatch = DeadLetterMessageMatch.Contains
        });
        contains.Single().ExceptionMessage.ShouldBe("Order 100% failed_badly");

        var containsSeveral = await all(new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "Order", ExceptionMessageMatch = DeadLetterMessageMatch.Contains
        });
        containsSeveral.Count.ShouldBe(3);

        // An exact match on part of a message finds nothing
        (await all(new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "Order", ExceptionMessageMatch = DeadLetterMessageMatch.Exact
        })).ShouldBeEmpty();

        // The match mode applies to the bulk operations as well
        var discarded = await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery
        {
            ExceptionMessage = "was fine", ExceptionMessageMatch = DeadLetterMessageMatch.Contains
        }, CancellationToken.None);

        if (supports(DeadLetterCapabilities.OperationCounts)) discarded.Affected.ShouldBe(1);
        (await all()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task archive_and_restore()
    {
        if (!supports(DeadLetterCapabilities.ArchiveAndRestore | DeadLetterCapabilities.FailureGroups)) return;

        await deadLetters(4, i => Failures.CardDeclined(i.ToString()));
        await deadLetters(2, i => Failures.OutOfStock(i.ToString()));

        var cards = (await all()).First(x => x.ExceptionMessage.StartsWith("Card")).FailureGroup!;

        var archived = await theDeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = cards },
            CancellationToken.None);
        archived.Affected.ShouldBe(4);

        // Archiving twice archives nothing new
        (await theDeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = cards },
            CancellationToken.None)).Affected.ShouldBe(0);

        // Excluded from queries, summaries and replays by default
        (await all()).Count.ShouldBe(2);
        (await theDeadLetters.SummarizeAllAsync(ServiceName, new DeadLetterSummaryOptions(), CancellationToken.None))
            .Single().Count.ShouldBe(2);
        (await theDeadLetters.SummarizeAllAsync(ServiceName, TimeRange.AllTime(), CancellationToken.None))
            .Sum(x => x.Count).ShouldBe(2);

        var replayArchived = await theDeadLetters.ReplayMatchingAsync(
            new DeadLetterEnvelopeQuery { FailureGroup = cards, Archived = DeadLetterArchiveFilter.Any },
            CancellationToken.None);
        replayArchived.Affected.ShouldBe(0);

        var onlyArchived = await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Only });
        onlyArchived.Count.ShouldBe(4);
        onlyArchived.ShouldAllBe(x => x.ArchivedAt != null && !x.Replayable);

        (await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Any })).Count.ShouldBe(6);

        // Restore, then replay
        var restored = await theDeadLetters.RestoreMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = cards },
            CancellationToken.None);
        restored.Affected.ShouldBe(4);

        (await all()).Count.ShouldBe(6);
        (await all()).ShouldAllBe(x => x.ArchivedAt == null);

        (await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = cards },
            CancellationToken.None)).Affected.ShouldBe(4);
    }

    [Fact]
    public async Task discard_by_id_reaches_an_archived_dead_letter()
    {
        if (!supports(DeadLetterCapabilities.ArchiveAndRestore)) return;

        var envelope = await deadLetter(Failures.CardDeclined("1"));
        await deadLetter(Failures.CardDeclined("2"));

        await theDeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery([envelope.Id]), CancellationToken.None);

        var discarded = await theDeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery([envelope.Id]),
            CancellationToken.None);
        discarded.Affected.ShouldBe(1);

        (await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Any })).Count.ShouldBe(1);
    }

    [Fact]
    public async Task dead_letters_recorded_before_fingerprinting_are_grouped_by_exception_type()
    {
        if (!supports(DeadLetterCapabilities.FailureGroups)) return;

        await deadLetters(3, i => Failures.CardDeclined(i.ToString()));
        await deadLetters(2, i => new DivideByZeroException(i.ToString()));

        await clearFailureGroupsAsync();

        // A new dead letter after the upgrade is fingerprinted as usual
        await deadLetter(Failures.CardDeclined("after"));

        var summaries = await theDeadLetters.SummarizeAllAsync(ServiceName, new DeadLetterSummaryOptions(),
            CancellationToken.None);

        summaries.Count.ShouldBe(3);

        var legacyCards = summaries.Single(x => x.Count == 3);
        legacyCards.PredatesFingerprinting.ShouldBeTrue();
        legacyCards.Key.ShouldBe(DeadLetterFingerprint.UnfingerprintedGroupFor("System.InvalidOperationException"));
        legacyCards.Title.ShouldBe("System.InvalidOperationException (recorded before failure fingerprinting)");

        summaries.Single(x => x.Count == 2).PredatesFingerprinting.ShouldBeTrue();

        var fresh = summaries.Single(x => x.Count == 1);
        fresh.PredatesFingerprinting.ShouldBeFalse();
        fresh.Key.ShouldNotBe(legacyCards.Key);

        // The legacy key selects exactly those rows, and works for the bulk operations too
        (await all(new DeadLetterEnvelopeQuery { FailureGroup = legacyCards.Key })).Count.ShouldBe(3);

        if (supports(DeadLetterCapabilities.OperationCounts))
        {
            (await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery { FailureGroup = legacyCards.Key },
                CancellationToken.None)).Affected.ShouldBe(3);
        }
    }

    [Fact]
    public async Task replay_history_survives_replay_and_failing_again()
    {
        if (!supports(DeadLetterCapabilities.ReplayHistory)) return;

        var envelope = await deadLetter(Failures.CardDeclined("1"));
        var bystander = await deadLetter(Failures.OutOfStock("1"));

        var first = (await theDeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id))!;
        first.ReplayCount.ShouldBe(0);
        first.LastReplayedAt.ShouldBeNull();
        first.FirstFailedAt.ShouldNotBeNull();
        var firstFailedAt = first.FirstFailedAt!.Value;

        for (var replay = 1; replay <= 2; replay++)
        {
            (await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery([envelope.Id]),
                CancellationToken.None)).Affected.ShouldBe(1);

            await moveReplayableDeadLettersAsync();
            (await theDeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id)).ShouldBeNull();

            // ...and it fails again
            await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, Failures.CardDeclined("again"));

            var again = (await theDeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id))!;
            again.ReplayCount.ShouldBe(replay);
            again.LastReplayedAt.ShouldNotBeNull();
            again.FirstFailedAt.ShouldBe(firstFailedAt);
            again.FailedAt!.Value.ShouldBeGreaterThanOrEqualTo(firstFailedAt);
        }

        // History is per message
        var other = (await theDeadLetters.DeadLetterEnvelopeByIdAsync(bystander.Id))!;
        other.ReplayCount.ShouldBe(0);
        other.LastReplayedAt.ShouldBeNull();
    }
}

/// <summary>
/// GH-5209. The relational stores also expire archived dead letters and old replay history
/// </summary>
public abstract class RelationalDeadLetterFailureGroupCompliance : DeadLetterFailureGroupCompliance
{
    [Fact]
    public async Task archived_dead_letters_expire_after_their_own_retention()
    {
        var keep = await deadLetter(Failures.CardDeclined("keep"));
        var archive = await deadLetter(Failures.CardDeclined("archive"));

        await theDeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery([archive.Id]), CancellationToken.None);

        var settings = new DurabilitySettings
        {
            DeadLetterQueueExpirationEnabled =
                theHost.Services.GetRequiredService<WolverineOptions>().Durability.DeadLetterQueueExpirationEnabled,
            ArchivedDeadLetterRetention = 1.Days()
        };

        // Not yet
        await expireDeadLettersAsync(settings, DateTimeOffset.UtcNow.AddHours(1));
        (await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Any })).Count.ShouldBe(2);

        // Two days on, the archived one is past its retention while the other is still within the default
        // dead letter expiration
        await expireDeadLettersAsync(settings, DateTimeOffset.UtcNow.AddDays(2));

        var remaining = await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Any });
        remaining.Single().Id.ShouldBe(keep.Id);
    }

    [Fact]
    public async Task old_replay_history_is_swept_by_the_expiration()
    {
        var envelope = await deadLetter(Failures.CardDeclined("1"));

        await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery([envelope.Id]), CancellationToken.None);
        await moveReplayableDeadLettersAsync();

        // Well past the dead letter expiration window, so the history goes
        await expireDeadLettersAsync(new DurabilitySettings(), DateTimeOffset.UtcNow.AddDays(30));

        await thePersistence.Inbox.MoveToDeadLetterStorageAsync(envelope, Failures.CardDeclined("again"));
        (await theDeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id))!.ReplayCount.ShouldBe(0);
    }
}
