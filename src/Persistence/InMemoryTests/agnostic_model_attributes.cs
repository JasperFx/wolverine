using JasperFx.CodeGeneration.Frames;
using JasperFx.Events.Documents;
using JasperFx.Events.InMemory;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace InMemoryTests;

// The same store-agnostic handlers MartenTests', PolecatTests' and FisherTests' agnostic_model_attributes
// run, against the in-memory prototyping store: [WriteModel] / [ReadModel] / [DeciderFunction] name no
// store, so this handler code runs unchanged once the application switches to a real one.
public class agnostic_model_attributes : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await TestHosts.StartAsync(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordDepositHandler))
                    .IncludeType(typeof(RecordWithdrawalHandler))
                    .IncludeType(typeof(ReadAccountBalanceHandler));
            },
            store => store.ConfigureProjections(x => x.Snapshot<Account>(SnapshotLifecycle.Inline)));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IDocumentSessionFactory Sessions => theHost.Services.GetRequiredService<IDocumentSessionFactory>();

    private async Task<Guid> givenAccount(decimal opening)
    {
        var streamId = Guid.NewGuid();
        await using var session = Sessions.LightweightSession();
        session.Events.StartStream<Account>(streamId, new AmountDeposited(opening));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<Account> loadAccount(Guid streamId)
    {
        await using var session = Sessions.LightweightSession();
        return (await session.Events.FetchLatest<Account>(streamId, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public void messaging_runs_without_a_durable_inbox_or_outbox()
    {
        theHost.GetRuntime().Storage.ShouldBeOfType<NullMessageStore>();
    }

    [Fact]
    public async Task write_model_loads_the_stream_and_appends_the_returned_events()
    {
        var streamId = await givenAccount(100m);

        await theHost.InvokeAsync(new RecordDeposit(streamId, 25m));

        (await loadAccount(streamId)).Balance.ShouldBe(125m);
    }

    [Fact]
    public async Task the_inline_snapshot_is_written_by_the_handlers_commit()
    {
        var streamId = await givenAccount(10m);

        await theHost.InvokeAsync(new RecordDeposit(streamId, 5m));

        await using var session = Sessions.QuerySession();
        (await session.LoadAsync<Account>(streamId, TestContext.Current.CancellationToken))!.Balance.ShouldBe(15m);
    }

    [Fact]
    public async Task write_model_applies_transaction_support()
    {
        // Chains compile lazily, so drive one message through first
        await theHost.InvokeAsync(new RecordDeposit(await givenAccount(1m), 1m));

        var chain = theHost.GetRuntime().Handlers.ChainFor<RecordDeposit>()!;

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(IDocumentSessionOperations.SaveChangesAsync))
            .ShouldBeTrue();

        chain.IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task decider_function_reads_the_identity_off_the_command()
    {
        var streamId = await givenAccount(80m);

        await theHost.InvokeAsync(new RecordWithdrawal(streamId, 30m));

        (await loadAccount(streamId)).Balance.ShouldBe(50m);
    }

    [Fact]
    public async Task read_model_resolves_the_current_state_without_appending()
    {
        var streamId = await givenAccount(42m);

        await theHost.InvokeAsync(new ReadAccountBalance(streamId));

        ReadAccountBalanceHandler.LastBalance.ShouldBe(42m);

        // FetchLatest, not FetchForWriting: reading must not have advanced the stream
        await using var session = Sessions.QuerySession();
        var state = await session.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state!.Version.ShouldBe(1);
    }

    [Fact]
    public async Task a_stale_expected_version_fails_and_appends_nothing()
    {
        var streamId = await givenAccount(100m);

        // Two writers fold the stream at version 1; the second to commit loses
        await using var first = Sessions.LightweightSession();
        var stream = await first.Events.FetchForWriting<Account>(streamId, TestContext.Current.CancellationToken);

        await theHost.InvokeAsync(new RecordDeposit(streamId, 1m));

        stream.AppendOne(new AmountDeposited(1000m));
        await Should.ThrowAsync<Exception>(() => first.SaveChangesAsync(TestContext.Current.CancellationToken));

        (await loadAccount(streamId)).Balance.ShouldBe(101m);
    }
}

public record AmountDeposited(decimal Amount);

public record AmountWithdrawn(decimal Amount);

public record RecordDeposit(Guid AccountId, decimal Amount);

public record RecordWithdrawal(Guid AccountId, decimal Amount);

public record ReadAccountBalance(Guid AccountId);

public class Account
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(AmountDeposited e) => Balance += e.Amount;
    public void Apply(AmountWithdrawn e) => Balance -= e.Amount;
}

public static class RecordDepositHandler
{
    public static AmountDeposited Handle(RecordDeposit command, [WriteModel] Account account)
        => new(command.Amount);
}

[DeciderFunction]
public static class RecordWithdrawalHandler
{
    public static AmountWithdrawn Handle(RecordWithdrawal command, Account account)
        => new(command.Amount);
}

public static class ReadAccountBalanceHandler
{
    public static decimal LastBalance { get; private set; }

    public static void Handle(ReadAccountBalance query, [ReadModel] Account account)
    {
        LastBalance = account.Balance;
    }
}
