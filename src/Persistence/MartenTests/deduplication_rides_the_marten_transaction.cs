using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-4505. A logical deduplication claim on a Marten-transactional chain is written INSIDE the Marten
/// session's transaction rather than on a connection of its own.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour these pin is the one GH-4501 tried to buy with a compensating release: a failed attempt
/// must not poison its logical id. The difference is where the correctness comes from. Claim-and-release
/// gets there by noticing the failure and undoing the claim, which means a <c>finally</c>, a status-code
/// test, and a window between the failure and the undo. Riding the transaction gets there by never having
/// claimed — a rollback takes the claim with it, and there is no window because there is nothing to undo.
/// </para>
/// <para>
/// So these assert on the generated source as well as on behaviour. A results-only test passes identically
/// against the claim-and-release implementation, which is exactly what this issue replaces.
/// </para>
/// </remarks>
public class deduplication_rides_the_marten_transaction : IAsyncLifetime
{
    private const string SchemaName = "tx_dedup";

    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public deduplication_rides_the_marten_transaction(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordPaymentHandler))
                    .IncludeType(typeof(FailingPaymentHandler))
                    .IncludeType(typeof(OptionallyKeyedHandler))
                    .IncludeType(typeof(AdjustLedgerHandler))
                    .IncludeType(typeof(RacingHandler))
                    .IncludeType(typeof(NothingElseHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertions below are counts rather than races.
                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = SchemaName;
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _host.ResetResourceState();

        // ResetResourceState clears Wolverine's own tables, the deduplication table included, but leaves
        // the application's Marten documents alone -- and the race test counts them.
        await _host.DocumentStore().Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(PaymentRecord));

        RecordPaymentHandler.Received.Clear();
        OptionallyKeyedHandler.Received.Clear();
        FailingPaymentHandler.Attempts = 0;
        RacingHandler.Arrived = 0;
        NothingElseHandler.Calls = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var chain = _host.GetRuntime().Handlers.ChainFor<T>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(chain.SourceCode);
        return chain.SourceCode;
    }

    [Fact]
    public void the_claim_is_queued_onto_the_session_and_nothing_is_released()
    {
        var code = sourceFor<RecordPayment>();

        // Queued onto the session, so it commits with the handler's work...
        code.ShouldContain($"{nameof(IMartenDeduplicator.QueueClaim)}(documentSession");

        // ...and therefore there is no compensating release, no try/finally around the chain to hold one,
        // and no status-code test to decide whether to run it. This is the whole point of the issue: those
        // three moving parts all have to stay right, and none of them exists on this path.
        code.ShouldNotContain("ReleaseAsync");
        code.ShouldNotContain("ReleaseDeduplicationClaimBeforeFailureResponse");
        code.ShouldNotContain("deduplicatedExecutionThrew");
    }

    [Fact]
    public async Task the_first_message_runs_and_a_replay_of_the_same_id_is_discarded()
    {
        await _host.SendMessageAndWaitAsync(new RecordPayment("first"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        // Same logical id, different payload, different Envelope.Id -- nothing but the logical id can
        // refuse this one.
        await _host.SendMessageAndWaitAsync(new RecordPayment("second"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        RecordPaymentHandler.Received.ShouldHaveSingleItem().ShouldBe("first");

        // And the claim really is in the database, committed by the Marten transaction rather than by a
        // connection of its own.
        (await claimCountAsync("invoice-17")).ShouldBe(1);
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        await _host.SendMessageAndWaitAsync(new RecordPayment("a"),
            new DeliveryOptions { DeduplicationId = "invoice-a" });
        await _host.SendMessageAndWaitAsync(new RecordPayment("b"),
            new DeliveryOptions { DeduplicationId = "invoice-b" });

        RecordPaymentHandler.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_handler_that_throws_never_claimed_the_id_at_all()
    {
        // The single most damaging way to get this wrong: the failed attempt leaves its claim behind, every
        // retry is refused as a duplicate of its own failure, and the work is silently never done while the
        // logs report successful deduplication.
        //
        // Claim-and-release survives this by DELETING the claim on the way out. Here there is nothing to
        // delete, because SaveChangesAsync never ran -- which is a stronger statement, and the assertion
        // below says so directly rather than inferring it from the retry.
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPayment(), new DeliveryOptions { DeduplicationId = "poison" });

        FailingPaymentHandler.Attempts.ShouldBe(1);
        (await claimCountAsync("poison")).ShouldBe(0);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPayment(), new DeliveryOptions { DeduplicationId = "poison" });

        FailingPaymentHandler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task an_unkeyed_message_on_an_optional_chain_costs_no_round_trip()
    {
        // An optional id opts out of the batched check on purpose: batch enlistment is unconditional code,
        // and a mixed stream's unkeyed traffic is supposed to behave exactly as if the feature were off --
        // including paying nothing for it. Only the guarded standalone form can promise that.
        var code = sourceFor<OptionallyKeyed>();
        code.ShouldContain("if (!string.IsNullOrWhiteSpace(");

        await _host.SendMessageAndWaitAsync(new OptionallyKeyed("x"));
        await _host.SendMessageAndWaitAsync(new OptionallyKeyed("y"));

        OptionallyKeyedHandler.Received.ShouldBe(["x", "y"]);
    }

    [Fact]
    public void the_check_shares_the_batch_with_the_aggregate_load()
    {
        // The composition GH-4501 was reported against, and the reason this design is cheaper rather than
        // merely tidier: the "has this been claimed?" query rides the round trip the chain was already
        // making for FetchForWriting. Claim-and-release pays a round trip to claim before that one, plus
        // another to release when the chain refuses.
        var code = sourceFor<AdjustLedger>();

        code.ShouldContain("CreateBatchQuery()");
        code.ShouldContain($"{nameof(IMartenDeduplicator.ClaimExistsQuery)}(");
        code.ShouldContain("FetchForWriting");

        // One batch, not one per read.
        code.Split("CreateBatchQuery()").Length.ShouldBe(2);

        // ...and no standalone check left behind alongside it.
        code.ShouldNotContain($"{nameof(IMartenDeduplicator.HasClaimAsync)}(");
    }

    [Fact]
    public async Task the_loser_of_a_concurrent_race_is_discarded_rather_than_dead_lettered()
    {
        // The one duplicate an uncommitted claim cannot detect: both callers read "not claimed" -- because
        // neither claim is committed yet and so neither is visible to the other -- both queue the INSERT,
        // and the second to commit trips the deduplication table's primary key. Left alone that escapes as
        // a raw Postgres unique violation.
        //
        // The barrier is the point of this test. Two concurrent sends would pass just as happily against a
        // build with no commit-race handling at all, because the optimistic check refuses the second one
        // whenever the first has already committed -- which, on a two-message local queue, it almost always
        // has. RacingHandler holds BOTH executions past their check before either is allowed to commit, so
        // the collision is forced rather than hoped for.
        var bus = _host.MessageBus();
        var command = new RacingCommand();

        await Task.WhenAll(
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()),
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()));

        RacingHandler.Arrived.ShouldBe(2, "both executions must get past the check for this to be a race");

        // Both ran, one commit survived. The loser came out as the ordinary refusal rather than an
        // exception -- InvokeAsync rethrows, so an unconverted Postgres error would have failed the await
        // above rather than reaching here.
        await using var query = _host.DocumentStore().QuerySession();
        var written = await query.Query<PaymentRecord>()
            .CountAsync(x => x.Name == RacingHandler.Marker, TestContext.Current.CancellationToken);

        written.ShouldBe(1);
        (await claimCountAsync(RacingHandler.DeduplicationId)).ShouldBe(1);
    }

    [Fact]
    public async Task the_claim_is_written_even_when_it_is_the_only_thing_in_the_unit_of_work()
    {
        // A commit with nothing else queued is the case that silently does nothing if the claim is not
        // real outstanding work: Marten's SaveChangesAsync short circuits on an empty unit of work, and a
        // claim that never lands means the endpoint reports itself as deduplicated while every replay runs.
        // Polecat and Fisher both had to be fixed upstream for exactly this shape.
        await _host.SendMessageAndWaitAsync(new NothingElse(),
            new DeliveryOptions { DeduplicationId = "only-claim" });

        (await claimCountAsync("only-claim")).ShouldBe(1);

        await _host.SendMessageAndWaitAsync(new NothingElse(),
            new DeliveryOptions { DeduplicationId = "only-claim" });

        NothingElseHandler.Calls.ShouldBe(1);
    }

    private static async Task<long> claimCountAsync(string key)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", key);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public record RecordPayment(string Name);

public record FailingPayment;

public record OptionallyKeyed(string Name);

public static class RecordPaymentHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(RecordPayment message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new PaymentRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

public static class FailingPaymentHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingPayment message, IDocumentSession session)
    {
        Attempts++;

        // Written first, so the test proves the ROLLBACK took the claim rather than proving the handler
        // never got far enough to queue anything.
        session.Store(new PaymentRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}

public static class OptionallyKeyedHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated(Required = false)]
    [Transactional]
    public static void Handle(OptionallyKeyed message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new PaymentRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

public class PaymentRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Carries its own deduplication id, so two concurrent <c>InvokeAsync</c> calls collide on one key without
/// a <c>DeliveryOptions</c> at each call site.
/// </summary>
[Deduplicated(Source = ValueSource.InputMember, Key = nameof(Key))]
public record RacingCommand
{
    public string Key => RacingHandler.DeduplicationId;
}

/// <summary>
/// Holds both executions past the deduplication check before either commits, which is what makes the
/// commit-time collision deterministic rather than a matter of timing.
/// </summary>
public static class RacingHandler
{
    public const string DeduplicationId = "forced-race";
    public const string Marker = "racer";

    public static int Arrived;

    private static readonly TaskCompletionSource _bothArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Transactional]
    public static async Task Handle(RacingCommand command, IDocumentSession session)
    {
        session.Store(new PaymentRecord { Id = Guid.NewGuid(), Name = Marker });

        if (Interlocked.Increment(ref Arrived) == 2)
        {
            _bothArrived.TrySetResult();
        }

        // Bounded, so a build that somehow lets only one execution through fails on the assertion rather
        // than hanging the suite.
        await _bothArrived.Task.WaitAsync(30.Seconds());
    }
}

public record NothingElse;

/// <summary>
/// Queues nothing but the deduplication claim, so the commit has only that one operation in its unit of
/// work.
/// </summary>
public static class NothingElseHandler
{
    public static int Calls;

    [Deduplicated]
    [Transactional]
    public static void Handle(NothingElse message, IDocumentSession session)
    {
        Calls++;
    }
}

public record AdjustLedger(Guid LedgerId, int Amount);

public record LedgerOpened;

public record LedgerAdjusted(int Amount);

public class Ledger
{
    public Guid Id { get; set; }
    public int Adjustments { get; set; }

    public void Apply(LedgerAdjusted _) => Adjustments++;
}

/// <summary>
/// The composition GH-4501 was reported against: a deduplicated command that loads an aggregate for
/// writing. Both reads are batchable, so they share one round trip.
/// </summary>
public static class AdjustLedgerHandler
{
    [Deduplicated]
    public static void Handle(AdjustLedger command, [WriteAggregate] IEventStream<Ledger> ledger)
    {
        ledger.AppendOne(new LedgerAdjusted(command.Amount));
    }
}
