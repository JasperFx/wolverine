using Fisher;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;

namespace FisherTests;

// GH-3907's own acceptance test, and the reason Fisher exists as the third sibling: writing
// Wolverine.Fisher as a third copy-paste of ~6,000 lines is the outcome the unification exists to
// prevent, and "Fisher being cheap to add" is how you know the unification is done.
//
// So this suite is deliberately the SAME suite as MartenTests' and PolecatTests'
// agnostic_model_attributes, against a third store: [WriteModel] / [ReadModel] / [DeciderFunction]
// name no store, resolve the owning one out of the persistence strategies registered on
// GenerationRules, and the handler code below would compile and run unchanged on any of the three.
public class agnostic_model_attributes : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(agnostic_model_attributes));

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordDepositHandler))
                    .IncludeType(typeof(RecordWithdrawalHandler))
                    .IncludeType(typeof(ReadAccountBalanceHandler));

                // Fisher is a single SQLite file, so Solo is the only durability mode that makes
                // sense - fisher#68 is explicit that leader election and agent distribution across a
                // cluster are not viable on one file.
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDatabase.Dispose();
    }

    private async Task<Guid> givenAccount(decimal opening)
    {
        var streamId = Guid.NewGuid();
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<Account>(streamId, new AmountDeposited(opening));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<Account> loadAccount(Guid streamId)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        return (await session.Events.AggregateStreamAsync<Account>(streamId,
            token: TestContext.Current.CancellationToken))!;
    }

    // The registration itself is worth pinning: Wolverine's durability tables land in a
    // SqliteMessageStore over the same file Fisher owns, which is what keeps the whole application
    // to one writer per file.
    [Fact]
    public void wolverine_persists_through_a_sqlite_message_database()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>();

        store.ShouldBeAssignableTo<IMessageDatabase>();
        store.GetType().Name.ShouldBe("SqliteMessageStore");
    }

    [Fact]
    public async Task write_model_loads_the_stream_and_appends_the_returned_events()
    {
        var streamId = await givenAccount(100m);

        await theHost.InvokeAsync(new RecordDeposit(streamId, 25m));

        (await loadAccount(streamId)).Balance.ShouldBe(125m);
    }

    [Fact]
    public async Task write_model_applies_transaction_support()
    {
        // Chains compile lazily, so drive one message through first
        await theHost.InvokeAsync(new RecordDeposit(await givenAccount(1m), 1m));

        var chain = theHost.GetRuntime().Handlers.ChainFor<RecordDeposit>()!;

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync))
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
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state!.Version.ShouldBe(1);
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
