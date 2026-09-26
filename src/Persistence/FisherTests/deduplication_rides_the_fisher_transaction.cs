using Fisher;
using Fisher.Linq;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
/// GH-4571, the Fisher half of GH-4505. A logical deduplication claim on a Fisher-transactional chain is
/// written INSIDE the Fisher session's transaction rather than on a connection of its own.
/// </summary>
/// <remarks>
/// <para>
/// The Marten twin queues an <c>IStorageOperation</c> onto the session's unit of work. That is not
/// available here, and on SQLite the alternative is not merely slower: Wolverine's message store is a
/// second <c>SqliteConnection</c> to the same file, and a write on it while the session holds the file's
/// write lock blocks on a transaction that is waiting for it. So the claim is enlisted as an
/// <c>ITransactionParticipant</c>, which is handed the live connection and transaction.
/// </para>
/// <para>
/// These assert on the generated source as well as on behaviour. A results-only test passes identically
/// against the claim-and-release implementation, which is exactly what this replaces.
/// </para>
/// </remarks>
public class deduplication_rides_the_fisher_transaction : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public deduplication_rides_the_fisher_transaction(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("transactional_deduplication");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordFisherPaymentHandler))
                    .IncludeType(typeof(FailingFisherPaymentHandler))
                    .IncludeType(typeof(OptionallyKeyedFisherHandler))
                    .IncludeType(typeof(ObservingFisherHandler))
                    .IncludeType(typeof(FisherRacingHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertions below are counts rather than races.
                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddFisher(m =>
                {
                    m.Connection(theDatabase.ConnectionString);
                    m.AutoCreateSchemaObjects = AutoCreate.All;
                }).ApplyAllDatabaseChangesOnStartup().IntegrateWithWolverine();
            }).StartAsync(TestContext.Current.CancellationToken);

        RecordFisherPaymentHandler.Received.Clear();
        OptionallyKeyedFisherHandler.Received.Clear();
        FailingFisherPaymentHandler.Attempts = 0;
        FisherRacingHandler.Arrived = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
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
    public void the_claim_is_enlisted_in_the_transaction_and_nothing_is_released()
    {
        var code = sourceFor<RecordFisherPayment>();

        // Enlisted on the session, so it commits with the handler's work...
        code.ShouldContain($"{nameof(IFisherDeduplicator.QueueClaim)}(documentSession");

        // ...and therefore there is no compensating release, no try/finally around the chain to hold one,
        // and no status-code test to decide whether to run it.
        code.ShouldNotContain("ReleaseAsync");
        code.ShouldNotContain("deduplicatedExecutionThrew");
    }

    [Fact]
    public async Task the_first_message_runs_and_a_replay_of_the_same_id_is_discarded()
    {
        await _host.SendMessageAndWaitAsync(new RecordFisherPayment("first"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        // Same logical id, different payload, different Envelope.Id.
        await _host.SendMessageAndWaitAsync(new RecordFisherPayment("second"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        RecordFisherPaymentHandler.Received.ShouldHaveSingleItem().ShouldBe("first");

        // And the claim really is in the database, committed by the Fisher transaction rather than by a
        // connection of its own.
        (await claimCountAsync("invoice-17")).ShouldBe(1);
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        await _host.SendMessageAndWaitAsync(new RecordFisherPayment("a"),
            new DeliveryOptions { DeduplicationId = "invoice-a" });
        await _host.SendMessageAndWaitAsync(new RecordFisherPayment("b"),
            new DeliveryOptions { DeduplicationId = "invoice-b" });

        RecordFisherPaymentHandler.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_handler_that_throws_never_claimed_the_id_at_all()
    {
        // The single most damaging way to get this wrong: the failed attempt leaves its claim behind,
        // every retry is refused as a duplicate of its own failure, and the work is silently never done.
        // Claim-and-release survives this by DELETING the claim; here there is nothing to delete, because
        // SaveChangesAsync never ran.
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingFisherPayment(),
                new DeliveryOptions { DeduplicationId = "poison" });

        FailingFisherPaymentHandler.Attempts.ShouldBe(1);
        (await claimCountAsync("poison")).ShouldBe(0);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingFisherPayment(),
                new DeliveryOptions { DeduplicationId = "poison" });

        FailingFisherPaymentHandler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task an_unkeyed_message_on_an_optional_chain_costs_no_round_trip()
    {
        var code = sourceFor<OptionallyKeyedFisher>();
        code.ShouldContain("if (!string.IsNullOrWhiteSpace(");

        await _host.SendMessageAndWaitAsync(new OptionallyKeyedFisher("x"));
        await _host.SendMessageAndWaitAsync(new OptionallyKeyedFisher("y"));

        OptionallyKeyedFisherHandler.Received.ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task no_observer_outside_the_transaction_can_see_the_claim_until_the_handler_commits()
    {
        // The one assertion that separates this from claim-and-release by BEHAVIOUR rather than by reading
        // the generated source. The old path wrote the claim on the message store's own connection and
        // committed it before the handler body ran, so a reader outside the handler's transaction saw it
        // immediately. Here it is invisible until SaveChangesAsync.
        ObservingFisherHandler.ConnectionString = theDatabase.ConnectionString;
        ObservingFisherHandler.ClaimsVisibleDuringHandler = -1;

        await _host.SendMessageAndWaitAsync(new ObservedFisherPayment(),
            new DeliveryOptions { DeduplicationId = "observed" });

        ObservingFisherHandler.ClaimsVisibleDuringHandler.ShouldBe(0,
            "the claim was committed before the handler's own work, which is the defect GH-4505 exists to close");

        // ...and it is there once the transaction commits, so the check above is not passing because the
        // claim was never written at all.
        (await claimCountAsync("observed")).ShouldBe(1);
    }

    [Fact]
    public async Task the_claim_is_written_even_when_it_is_the_only_thing_in_the_unit_of_work()
    {
        // A commit with nothing else enlisted is the case that silently does nothing if the claim is not
        // real outstanding work. Fisher needed an upstream fix (shipped in 0.5.3) so SaveChangesAsync
        // still runs queued ITransactionParticipants with no document operations outstanding --
        // outbox_with_an_empty_unit_of_work pins that -- and a claim-only commit is exactly that shape.
        await _host.SendMessageAndWaitAsync(new OptionallyKeyedFisher("only"),
            new DeliveryOptions { DeduplicationId = "only-claim" });

        (await claimCountAsync("only-claim")).ShouldBe(1);
    }

    [Fact]
    public async Task the_loser_of_a_concurrent_race_is_discarded_rather_than_dead_lettered()
    {
        // The one duplicate an uncommitted claim cannot detect: both callers read "not claimed" -- because
        // neither claim is committed yet and so neither is visible to the other -- both enlist the INSERT,
        // and the second to commit trips the deduplication table's primary key.
        //
        // The barrier is the point. Two concurrent sends would pass just as happily against a build with no
        // commit-race handling at all, because the optimistic check refuses the second one whenever the
        // first has already committed. FisherRacingHandler holds BOTH executions past their check before
        // either is allowed to commit, so the collision is forced rather than hoped for.
        //
        // SQLite has one writer, so the two commits serialize on the file's write lock rather than racing
        // inside a transaction -- but they still both hold a claim the other cannot see, which is what
        // makes the collision happen at all. The single-writer model changes when the loser finds out, not
        // whether it does.
        var bus = _host.MessageBus();
        var command = new FisherRacingCommand();

        await Task.WhenAll(
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()),
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()));

        FisherRacingHandler.Arrived.ShouldBe(2, "both executions must get past the check for this to be a race");

        // Both ran, one commit survived. The loser came out as the ordinary refusal rather than an
        // exception -- InvokeAsync rethrows, so an unconverted SqliteException would have failed the await.
        await using var query = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var written = (await query.Query<FisherPaymentRecord>()
                .Where(x => x.Name == FisherRacingHandler.Marker)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Count;

        written.ShouldBe(1);
        (await claimCountAsync(FisherRacingHandler.DeduplicationId)).ShouldBe(1);
    }

    private async Task<int> claimCountAsync(string key)
    {
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();

        // Fisher sets SchemaNameIsTablePrefix, so the deduplication table is ONE prefixed identifier
        // rather than schema.table, and the prefix follows the store's schema name. Looked up rather than
        // spelled out, so this asserts against whatever the integration actually provisioned.
        cmd.CommandText =
            $"select count(*) from {await deduplicationTableAsync(conn)} where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", key);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> deduplicationTableAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "select name from sqlite_master where type = 'table' and name like '%wolverine_deduplication'";

        var name = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        name.ShouldNotBeNull("the deduplication table was never provisioned");

        return (string)name;
    }
}

public record RecordFisherPayment(string Name);

public record FailingFisherPayment;

public record OptionallyKeyedFisher(string Name);

public class FisherPaymentRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class RecordFisherPaymentHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(RecordFisherPayment message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new FisherPaymentRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

public static class FailingFisherPaymentHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingFisherPayment message, IDocumentSession session)
    {
        Attempts++;

        // Written first, so the test proves the ROLLBACK took the claim rather than proving the handler
        // never got far enough to enlist anything.
        session.Store(new FisherPaymentRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}

public record ObservedFisherPayment;

/// <summary>
/// Reads the deduplication table from a connection of its own, from inside the handler, while the
/// session's transaction is still open.
/// </summary>
public static class ObservingFisherHandler
{
    public static string ConnectionString = string.Empty;
    public static int ClaimsVisibleDuringHandler = -1;

    [Deduplicated]
    [Transactional]
    public static async Task Handle(ObservedFisherPayment message, IDocumentSession session)
    {
        session.Store(new FisherPaymentRecord { Id = Guid.NewGuid(), Name = "observed" });

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();

        await using var table = conn.CreateCommand();
        table.CommandText =
            "select name from sqlite_master where type = 'table' and name like '%wolverine_deduplication'";
        var tableName = (string)(await table.ExecuteScalarAsync())!;

        // WAL is on for the async daemon, so this reader does not contend with the session's write lock.
        await using var count = conn.CreateCommand();
        count.CommandText = $"select count(*) from {tableName} where deduplication_id = 'observed'";

        ClaimsVisibleDuringHandler = Convert.ToInt32(await count.ExecuteScalarAsync());
    }
}

public static class OptionallyKeyedFisherHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated(Required = false)]
    [Transactional]
    public static void Handle(OptionallyKeyedFisher message, IDocumentSession session)
    {
        Received.Add(message.Name);
    }
}

/// <summary>
/// Carries its own deduplication id, so two concurrent <c>InvokeAsync</c> calls collide on one key without
/// a <c>DeliveryOptions</c> at each call site.
/// </summary>
[Deduplicated(Source = ValueSource.InputMember, Key = nameof(Key))]
public record FisherRacingCommand
{
    public string Key => FisherRacingHandler.DeduplicationId;
}

/// <summary>
/// Holds both executions past the deduplication check before either commits, which is what makes the
/// commit-time collision deterministic rather than a matter of timing.
/// </summary>
public static class FisherRacingHandler
{
    public const string DeduplicationId = "forced-race";
    public const string Marker = "racer";

    public static int Arrived;

    private static readonly TaskCompletionSource _bothArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Transactional]
    public static async Task Handle(FisherRacingCommand command, IDocumentSession session)
    {
        session.Store(new FisherPaymentRecord { Id = Guid.NewGuid(), Name = Marker });

        if (Interlocked.Increment(ref Arrived) == 2)
        {
            _bothArrived.TrySetResult();
        }

        // Bounded, so a build that somehow lets only one execution through fails on the assertion rather
        // than hanging the suite.
        await _bothArrived.Task.WaitAsync(30.Seconds());
    }
}
