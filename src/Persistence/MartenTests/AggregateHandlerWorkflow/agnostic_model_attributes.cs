using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Tracking;
using MartenConcurrencyStyle = Wolverine.Marten.ConcurrencyStyle;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-3907: [WriteModel] / [ReadModel] / [DeciderFunction] are the persistence-strategy-agnostic
// vocabulary in Wolverine core. Nothing about them names Marten - they resolve the owning store out
// of the persistence strategies registered on GenerationRules - so this suite proves they light up
// against a Marten-backed host, and that the Marten-named attributes they replace still behave
// identically now that those are shells over the same base.
public class agnostic_model_attributes : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RecordDepositHandler))
                    .IncludeType(typeof(RecordWithdrawalHandler))
                    .IncludeType(typeof(ReadAccountBalanceHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "agnostic_model_attributes";
                }).IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> givenAccount(decimal opening)
    {
        var streamId = Guid.NewGuid();
        await using var session = theHost.DocumentStore().LightweightSession();
        session.Events.StartStream<Account>(streamId, new AmountDeposited(opening));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<Account> loadAccount(Guid streamId)
    {
        await using var session = theHost.DocumentStore().LightweightSession();
        return (await session.Events.AggregateStreamAsync<Account>(streamId,
            token: TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task write_model_loads_the_stream_and_appends_the_returned_events()
    {
        var streamId = await givenAccount(100m);

        await theHost.InvokeAsync(new RecordDeposit(streamId, 25m));

        (await loadAccount(streamId)).Balance.ShouldBe(125m);
    }

    [Fact]
    public async Task write_model_applies_transaction_support_the_same_way_write_aggregate_does()
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
        await using var session = theHost.DocumentStore().LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state!.Version.ShouldBe(1);
    }
}

// The Marten-named attributes are shells over the core ones as of GH-3907. These pin the
// relationship itself, because "still compiles" is most of what keeps existing user code working,
// and a shell that silently stopped deriving would still compile at the *declaration* site.
public class marten_aggregate_attributes_are_shells_over_the_core_vocabulary
{
    [Fact]
    public void write_aggregate_is_a_write_model()
    {
        new WriteAggregateAttribute().ShouldBeAssignableTo<WriteModelAttribute>();
        new ConsistentAggregateAttribute().ShouldBeAssignableTo<WriteModelAttribute>();
    }

    [Fact]
    public void read_aggregate_is_a_read_model()
    {
        new ReadAggregateAttribute().ShouldBeAssignableTo<ReadModelAttribute>();
    }

    [Fact]
    public void aggregate_handler_is_a_decider_function()
    {
        new AggregateHandlerAttribute().ShouldBeAssignableTo<DeciderFunctionAttribute>();
        new ConsistentAggregateHandlerAttribute().ShouldBeAssignableTo<DeciderFunctionAttribute>();
    }

    [Fact]
    public void route_or_parameter_name_still_reaches_the_base()
    {
        new WriteAggregateAttribute("invoiceId").RouteOrParameterName.ShouldBe("invoiceId");
    }

    // Wolverine.Marten.ConcurrencyStyle stays public vocabulary, so [WriteAggregate] shadows
    // LoadStyle to keep that spelling compiling. The shadow has to actually reach the base -
    // an unforwarded setter would silently downgrade every exclusive lock to optimistic.
    [Fact]
    public void marten_concurrency_style_forwards_to_the_core_load_style()
    {
        var att = new WriteAggregateAttribute();

        att.LoadStyle.ShouldBe(MartenConcurrencyStyle.Optimistic);
        ((WriteModelAttribute)att).LoadStyle
            .ShouldBe(ModelConcurrencyStyle.Optimistic);

        att.LoadStyle = MartenConcurrencyStyle.Exclusive;

        att.LoadStyle.ShouldBe(MartenConcurrencyStyle.Exclusive);
        ((WriteModelAttribute)att).LoadStyle
            .ShouldBe(ModelConcurrencyStyle.Exclusive);
    }

    [Fact]
    public void aggregate_handler_load_style_constructor_forwards_to_the_core_load_style()
    {
        new AggregateHandlerAttribute(MartenConcurrencyStyle.Exclusive)
            .LoadStyle.ShouldBe(ModelConcurrencyStyle.Exclusive);

        new AggregateHandlerAttribute()
            .LoadStyle.ShouldBe(ModelConcurrencyStyle.Optimistic);

        // ConsistentAggregateHandler goes through the same ctor and adds its own flag
        var consistent = new ConsistentAggregateHandlerAttribute(MartenConcurrencyStyle.Exclusive);
        consistent.LoadStyle.ShouldBe(ModelConcurrencyStyle.Exclusive);
        consistent.AlwaysEnforceConsistency.ShouldBeTrue();
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
