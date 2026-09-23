using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Linq;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PolecatTests;

/// <summary>
/// GH-4570, the Polecat half of GH-4505. A logical deduplication claim on a Polecat-transactional chain is
/// written INSIDE the Polecat session's transaction rather than on a connection of its own.
/// </summary>
/// <remarks>
/// <para>
/// The Marten twin queues an <c>IStorageOperation</c> onto the session's unit of work. That is not
/// available here: Wolverine's message store on Polecat is built from
/// <c>SqlClientFactory.CreateDataSource(connectionString)</c> — the same SQL Server database, a different
/// connection pool — so the claim is enlisted as an <c>ITransactionParticipant</c>, which is handed the
/// live connection and transaction.
/// </para>
/// <para>
/// These assert on the generated source as well as on behaviour. A results-only test passes identically
/// against the claim-and-release implementation, which is exactly what this replaces.
/// </para>
/// </remarks>
public class deduplication_rides_the_polecat_transaction : IAsyncLifetime
{
    private const string SchemaName = "tx_dedup";

    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public deduplication_rides_the_polecat_transaction(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordPolecatPaymentHandler))
                    .IncludeType(typeof(FailingPolecatPaymentHandler))
                    .IncludeType(typeof(OptionallyKeyedPolecatHandler))
                    .IncludeType(typeof(PolecatRacingHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.EnableMessageDeduplication = true;
                opts.Durability.DeduplicationWindow = 1.Hours();

                // Discard rather than retry, so each Send is exactly one handler attempt and the
                // poison-check assertions below are counts rather than races.
                opts.OnException<DivideByZeroException>().Discard();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = SchemaName;
                }).IntegrateWithWolverine();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _host.ResetResourceState();

        // ResetResourceState clears Wolverine's own tables, the deduplication table included, but leaves
        // the application's Polecat documents alone -- and the race test counts them. Without this the
        // class passes on a virgin database and fails on every rerun.
        await _host.DocumentStore().Advanced.Clean.DeleteAllDocumentsAsync();

        RecordPolecatPaymentHandler.Received.Clear();
        OptionallyKeyedPolecatHandler.Received.Clear();
        FailingPolecatPaymentHandler.Attempts = 0;
        PolecatRacingHandler.Arrived = 0;
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
    public void the_claim_is_enlisted_in_the_transaction_and_nothing_is_released()
    {
        var code = sourceFor<RecordPolecatPayment>();

        // Enlisted on the session, so it commits with the handler's work...
        code.ShouldContain($"{nameof(IPolecatDeduplicator.QueueClaim)}(documentSession");

        // ...and therefore there is no compensating release, no try/finally around the chain to hold one,
        // and no status-code test to decide whether to run it.
        code.ShouldNotContain("ReleaseAsync");
        code.ShouldNotContain("deduplicatedExecutionThrew");
    }

    [Fact]
    public async Task the_first_message_runs_and_a_replay_of_the_same_id_is_discarded()
    {
        await _host.SendMessageAndWaitAsync(new RecordPolecatPayment("first"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        // Same logical id, different payload, different Envelope.Id.
        await _host.SendMessageAndWaitAsync(new RecordPolecatPayment("second"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        RecordPolecatPaymentHandler.Received.ShouldHaveSingleItem().ShouldBe("first");

        // And the claim really is in the database, committed by the Polecat transaction rather than by a
        // connection of its own.
        (await claimCountAsync("invoice-17")).ShouldBe(1);
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        await _host.SendMessageAndWaitAsync(new RecordPolecatPayment("a"),
            new DeliveryOptions { DeduplicationId = "invoice-a" });
        await _host.SendMessageAndWaitAsync(new RecordPolecatPayment("b"),
            new DeliveryOptions { DeduplicationId = "invoice-b" });

        RecordPolecatPaymentHandler.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_handler_that_throws_never_claimed_the_id_at_all()
    {
        // The single most damaging way to get this wrong: the failed attempt leaves its claim behind,
        // every retry is refused as a duplicate of its own failure, and the work is silently never done.
        // Claim-and-release survives this by DELETING the claim; here there is nothing to delete, because
        // SaveChangesAsync never ran.
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPolecatPayment(),
                new DeliveryOptions { DeduplicationId = "poison" });

        FailingPolecatPaymentHandler.Attempts.ShouldBe(1);
        (await claimCountAsync("poison")).ShouldBe(0);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPolecatPayment(),
                new DeliveryOptions { DeduplicationId = "poison" });

        FailingPolecatPaymentHandler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task an_unkeyed_message_on_an_optional_chain_costs_no_round_trip()
    {
        var code = sourceFor<OptionallyKeyedPolecat>();
        code.ShouldContain("if (!string.IsNullOrWhiteSpace(");

        await _host.SendMessageAndWaitAsync(new OptionallyKeyedPolecat("x"));
        await _host.SendMessageAndWaitAsync(new OptionallyKeyedPolecat("y"));

        OptionallyKeyedPolecatHandler.Received.ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task the_claim_is_written_even_when_it_is_the_only_thing_in_the_unit_of_work()
    {
        // A commit with nothing else enlisted is the case that silently does nothing if the claim is not
        // real outstanding work. Polecat needed an upstream fix (polecat#161, shipped in 4.2.1) so that
        // SaveChangesAsync still runs queued ITransactionParticipants with no document operations
        // outstanding, and a claim-only commit is exactly that shape.
        await _host.SendMessageAndWaitAsync(new OptionallyKeyedPolecat("only"),
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
        // first has already committed. PolecatRacingHandler holds BOTH executions past their check before
        // either is allowed to commit, so the collision is forced rather than hoped for.
        var bus = _host.MessageBus();
        var command = new PolecatRacingCommand();

        await Task.WhenAll(
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()),
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()));

        PolecatRacingHandler.Arrived.ShouldBe(2, "both executions must get past the check for this to be a race");

        // Both ran, one commit survived. The loser came out as the ordinary refusal rather than an
        // exception -- InvokeAsync rethrows, so an unconverted SqlException would have failed the await.
        await using var query = _host.DocumentStore().QuerySession();
        var written = await query.Query<PolecatPaymentRecord>()
            .CountAsync(x => x.Name == PolecatRacingHandler.Marker, TestContext.Current.CancellationToken);

        written.ShouldBe(1);
        (await claimCountAsync(PolecatRacingHandler.DeduplicationId)).ShouldBe(1);
    }

    private static async Task<int> claimCountAsync(string key)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", key);

        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public record RecordPolecatPayment(string Name);

public record FailingPolecatPayment;

public record OptionallyKeyedPolecat(string Name);

public class PolecatPaymentRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class RecordPolecatPaymentHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated]
    [Transactional]
    public static void Handle(RecordPolecatPayment message, IDocumentSession session)
    {
        Received.Add(message.Name);
        session.Store(new PolecatPaymentRecord { Id = Guid.NewGuid(), Name = message.Name });
    }
}

public static class FailingPolecatPaymentHandler
{
    public static int Attempts;

    [Deduplicated]
    [Transactional]
    public static void Handle(FailingPolecatPayment message, IDocumentSession session)
    {
        Attempts++;

        // Written first, so the test proves the ROLLBACK took the claim rather than proving the handler
        // never got far enough to enlist anything.
        session.Store(new PolecatPaymentRecord { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }
}

public static class OptionallyKeyedPolecatHandler
{
    public static readonly List<string> Received = [];

    [Deduplicated(Required = false)]
    [Transactional]
    public static void Handle(OptionallyKeyedPolecat message, IDocumentSession session)
    {
        Received.Add(message.Name);
    }
}

/// <summary>
/// Carries its own deduplication id, so two concurrent <c>InvokeAsync</c> calls collide on one key without
/// a <c>DeliveryOptions</c> at each call site.
/// </summary>
[Deduplicated(Source = ValueSource.InputMember, Key = nameof(Key))]
public record PolecatRacingCommand
{
    public string Key => PolecatRacingHandler.DeduplicationId;
}

/// <summary>
/// Holds both executions past the deduplication check before either commits, which is what makes the
/// commit-time collision deterministic rather than a matter of timing.
/// </summary>
public static class PolecatRacingHandler
{
    public const string DeduplicationId = "forced-race";
    public const string Marker = "racer";

    public static int Arrived;

    private static readonly TaskCompletionSource _bothArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Transactional]
    public static async Task Handle(PolecatRacingCommand command, IDocumentSession session)
    {
        session.Store(new PolecatPaymentRecord { Id = Guid.NewGuid(), Name = Marker });

        if (Interlocked.Increment(ref Arrived) == 2)
        {
            _bothArrived.TrySetResult();
        }

        // Bounded, so a build that somehow lets only one execution through fails on the assertion rather
        // than hanging the suite.
        await _bothArrived.Task.WaitAsync(30.Seconds());
    }
}
