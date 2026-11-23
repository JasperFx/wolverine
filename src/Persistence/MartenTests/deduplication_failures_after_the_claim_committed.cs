using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-5835. A <c>[Deduplicated]</c> message that fails after its own claim committed fails as itself, rather than
/// being discarded as the duplicate of a concurrent caller that never existed.
/// </summary>
public class deduplication_failures_after_the_claim_committed : IAsyncLifetime
{
    private const string SchemaName = "tx_dedup_own_claim";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AppendOwnClaimEventHandler))
                    .IncludeType(typeof(OwnClaimSideEffectHandler))
                    .IncludeType(typeof(FailingParticipantOwnClaimHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.Policies.OnAnyException().MoveToErrorQueue();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = SchemaName;
                    m.Events.DatabaseSchemaName = SchemaName;
                    m.Events.EnableSideEffectsOnInlineProjections = true;
                    m.Projections.Add(new OwnClaimProjection(), ProjectionLifecycle.Inline);
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _host.ResetResourceState();

        var options = ((DocumentStore)_host.DocumentStore()).Options;
        if (options.Events.MessageOutbox is not FailingAfterCommitOutbox)
        {
            options.Events.MessageOutbox = new FailingAfterCommitOutbox(options.Events.MessageOutbox);
        }

        FailingAfterCommitOutbox.Fail = false;
        AppendOwnClaimEventHandler.Runs = 0;
        FailingParticipantOwnClaimHandler.Runs = 0;
    }

    public async ValueTask DisposeAsync()
    {
        FailingAfterCommitOutbox.Fail = false;
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_failure_in_the_inline_projection_message_batch_after_the_commit_is_not_discarded_as_a_duplicate()
    {
        var id = Guid.NewGuid().ToString();
        var message = new AppendOwnClaimEvent(Guid.NewGuid());
        FailingAfterCommitOutbox.Fail = true;

        var tracked = await sendAsync(message, id);

        (await claimCountAsync(id)).ShouldBe(1);
        AppendOwnClaimEventHandler.Runs.ShouldBe(1);

        tracked.AllExceptions().ShouldHaveSingleItem().ShouldBeOfType<DivideByZeroException>();
        tracked.MovedToErrorQueue.MessagesOf<AppendOwnClaimEvent>().ShouldHaveSingleItem();
        tracked.MessageSucceeded.MessagesOf<AppendOwnClaimEvent>().ShouldBeEmpty();

        FailingAfterCommitOutbox.Fail = false;
        var repeat = await sendAsync(message, id);

        repeat.AllExceptions().ShouldBeEmpty();
        repeat.MessageSucceeded.MessagesOf<AppendOwnClaimEvent>().ShouldHaveSingleItem();
        AppendOwnClaimEventHandler.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task a_transaction_participant_that_fails_after_the_commit_is_not_discarded_as_a_duplicate()
    {
        var id = Guid.NewGuid().ToString();

        var tracked = await sendAsync(new FailingParticipantOwnClaim(), id);

        (await claimCountAsync(id)).ShouldBe(1);
        FailingParticipantOwnClaimHandler.Runs.ShouldBe(1);

        tracked.AllExceptions().ShouldHaveSingleItem().ShouldBeOfType<DivideByZeroException>();
        tracked.MovedToErrorQueue.MessagesOf<FailingParticipantOwnClaim>().ShouldHaveSingleItem();
        tracked.MessageSucceeded.MessagesOf<FailingParticipantOwnClaim>().ShouldBeEmpty();

        var repeat = await sendAsync(new FailingParticipantOwnClaim(), id);

        repeat.AllExceptions().ShouldBeEmpty();
        repeat.MessageSucceeded.MessagesOf<FailingParticipantOwnClaim>().ShouldHaveSingleItem();
        FailingParticipantOwnClaimHandler.Runs.ShouldBe(1);
    }

    private Task<ITrackedSession> sendAsync<T>(T message, string deduplicationId) where T : class
        => _host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(message, new DeliveryOptions { DeduplicationId = deduplicationId });

    private static async Task<long> claimCountAsync(string id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication_hashed where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", id);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public record AppendOwnClaimEvent(Guid StreamId);

public record OwnClaimEventAppended;

public record OwnClaimSideEffect(Guid StreamId);

public record FailingParticipantOwnClaim;

public class OwnClaimDocument
{
    public Guid Id { get; set; }
}

public class OwnClaimProjection : SingleStreamProjection<OwnClaimDocument, Guid>
{
    public static OwnClaimDocument Create(OwnClaimEventAppended _, IEvent e) => new() { Id = e.StreamId };

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<OwnClaimDocument> slice)
    {
        if (slice.Snapshot is not null) slice.PublishMessage(new OwnClaimSideEffect(slice.Snapshot.Id));
        return ValueTask.CompletedTask;
    }
}

public static class AppendOwnClaimEventHandler
{
    public static int Runs;

    [Deduplicated]
    [Transactional]
    public static void Handle(AppendOwnClaimEvent message, IDocumentSession session)
    {
        Interlocked.Increment(ref Runs);
        session.Events.Append(message.StreamId, new OwnClaimEventAppended());
    }
}

public static class OwnClaimSideEffectHandler
{
    public static void Handle(OwnClaimSideEffect _)
    {
    }
}

public static class FailingParticipantOwnClaimHandler
{
    public static int Runs;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingParticipantOwnClaim message, IDocumentSession session)
    {
        Interlocked.Increment(ref Runs);
        ((ITransactionParticipantRegistrar)session).AddTransactionParticipant(new FailsAfterCommitParticipant());
        session.Store(new OwnClaimDocument { Id = Guid.NewGuid() });
    }
}

public class FailsAfterCommitParticipant : ITransactionParticipant
{
    public Task BeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Task.CompletedTask;

    public Task AfterCommitAsync(CancellationToken token)
        => throw new DivideByZeroException("The commit call failed after the database committed");
}

public class FailingAfterCommitOutbox : IMessageOutbox
{
    public static volatile bool Fail;

    private readonly IMessageOutbox _inner;

    public FailingAfterCommitOutbox(IMessageOutbox inner) => _inner = inner;

    public async ValueTask<IMessageBatch> CreateBatch(DocumentSessionBase session)
        => new Batch(await _inner.CreateBatch(session));

    private class Batch : IMessageBatch
    {
        private readonly IMessageBatch _inner;

        public Batch(IMessageBatch inner) => _inner = inner;

        public ValueTask PublishAsync<T>(T message, string tenantId) => _inner.PublishAsync(message, tenantId);

        public ValueTask PublishAsync<T>(T message, MessageMetadata metadata) => _inner.PublishAsync(message, metadata);

        public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
            => _inner.BeforeCommitAsync(session, commit, token);

        public async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            await _inner.AfterCommitAsync(session, commit, token);
            if (Fail) throw new DivideByZeroException("The inline projection's message batch failed after the commit");
        }
    }
}
