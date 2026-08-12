using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Events;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;
using Wolverine.Tracking;
using PolecatConcurrencyStyle = Wolverine.Polecat.ConcurrencyStyle;

namespace PolecatTests.AggregateHandlerWorkflow;

// GH-3907: the twin of MartenTests/AggregateHandlerWorkflow/agnostic_model_attributes.cs. Same
// handlers, same attributes, a different store underneath - which is the whole claim. Nothing in
// [WriteModel] / [ReadModel] / [DeciderFunction] names Marten or Polecat; each resolves the owning
// store out of the persistence strategies registered on GenerationRules.
public class agnostic_model_attributes : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "agnostic_models";
                        m.Projections.Snapshot<PcAccount>(SnapshotLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<Guid> givenAccount(decimal opening)
    {
        await using var session = theStore.LightweightSession();
        var action = session.Events.StartStream<PcAccount>(new PcAmountDeposited(opening));
        await session.SaveChangesAsync();

        return action.Id;
    }

    private async Task<PcAccount> loadAccount(Guid streamId)
    {
        await using var session = theStore.LightweightSession();
        var account = await session.LoadAsync<PcAccount>(streamId);
        account.ShouldNotBeNull();
        return account;
    }

    [Fact]
    public async Task write_model_loads_the_stream_and_appends_the_returned_events()
    {
        var streamId = await givenAccount(100m);

        await theHost.InvokeAsync(new PcRecordDeposit(streamId, 25m));

        (await loadAccount(streamId)).Balance.ShouldBe(125m);
    }

    [Fact]
    public async Task write_model_applies_transaction_support_the_same_way_write_aggregate_does()
    {
        // Chains compile lazily, so drive one message through first
        await theHost.InvokeAsync(new PcRecordDeposit(await givenAccount(1m), 1m));

        var chain = theHost.GetRuntime().Handlers.ChainFor<PcRecordDeposit>()!;

        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync))
            .ShouldBeTrue();

        chain.IsTransactional.ShouldBeTrue();
    }

    [Fact]
    public async Task decider_function_reads_the_identity_off_the_command()
    {
        var streamId = await givenAccount(80m);

        await theHost.InvokeAsync(new PcRecordWithdrawal(streamId, 30m));

        (await loadAccount(streamId)).Balance.ShouldBe(50m);
    }

    [Fact]
    public async Task read_model_resolves_the_current_state_without_appending()
    {
        var streamId = await givenAccount(42m);

        await theHost.InvokeAsync(new PcReadAccountBalance(streamId));

        PcReadAccountBalanceHandler.LastBalance.ShouldBe(42m);

        // FetchLatest, not FetchForWriting: reading must not have advanced the stream
        await using var session = theStore.LightweightSession();
        var state = await session.Events.FetchStreamStateAsync(streamId, TestContext.Current.CancellationToken);
        state!.Version.ShouldBe(1);
    }
}

// The Polecat-named attributes are shells over the core ones as of GH-3907. These pin the
// relationship itself, because "still compiles" is most of what keeps existing user code working,
// and a shell that silently stopped deriving would still compile at the *declaration* site.
public class polecat_aggregate_attributes_are_shells_over_the_core_vocabulary
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

    // GH-3911
    [Fact]
    public void boundary_model_is_a_dcb_model()
    {
        new BoundaryModelAttribute().ShouldBeAssignableTo<DcbModelAttribute>();
    }

    [Fact]
    public void route_or_parameter_name_still_reaches_the_base()
    {
        new WriteAggregateAttribute("invoiceId").RouteOrParameterName.ShouldBe("invoiceId");
    }

    // Wolverine.Polecat.ConcurrencyStyle stays public vocabulary, so [WriteAggregate] shadows
    // LoadStyle to keep that spelling compiling. The shadow has to actually reach the base -
    // an unforwarded setter would silently downgrade every exclusive lock to optimistic.
    [Fact]
    public void polecat_concurrency_style_forwards_to_the_core_load_style()
    {
        var att = new WriteAggregateAttribute();

        att.LoadStyle.ShouldBe(PolecatConcurrencyStyle.Optimistic);
        ((WriteModelAttribute)att).LoadStyle.ShouldBe(ModelConcurrencyStyle.Optimistic);

        att.LoadStyle = PolecatConcurrencyStyle.Exclusive;

        att.LoadStyle.ShouldBe(PolecatConcurrencyStyle.Exclusive);
        ((WriteModelAttribute)att).LoadStyle.ShouldBe(ModelConcurrencyStyle.Exclusive);
    }

    [Fact]
    public void aggregate_handler_load_style_constructor_forwards_to_the_core_load_style()
    {
        new AggregateHandlerAttribute(PolecatConcurrencyStyle.Exclusive)
            .LoadStyle.ShouldBe(ModelConcurrencyStyle.Exclusive);

        new AggregateHandlerAttribute()
            .LoadStyle.ShouldBe(ModelConcurrencyStyle.Optimistic);

        var consistent = new ConsistentAggregateHandlerAttribute(PolecatConcurrencyStyle.Exclusive);
        consistent.LoadStyle.ShouldBe(ModelConcurrencyStyle.Exclusive);
        consistent.AlwaysEnforceConsistency.ShouldBeTrue();
    }
}

public record PcAmountDeposited(decimal Amount);

public record PcAmountWithdrawn(decimal Amount);

public record PcRecordDeposit(Guid PcAccountId, decimal Amount);

public record PcRecordWithdrawal(Guid PcAccountId, decimal Amount);

public record PcReadAccountBalance(Guid PcAccountId);

public class PcAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public void Apply(PcAmountDeposited e) => Balance += e.Amount;
    public void Apply(PcAmountWithdrawn e) => Balance -= e.Amount;
}

public static class PcRecordDepositHandler
{
    public static PcAmountDeposited Handle(PcRecordDeposit command, [WriteModel] PcAccount account)
        => new(command.Amount);
}

[DeciderFunction]
public static class PcRecordWithdrawalHandler
{
    public static PcAmountWithdrawn Handle(PcRecordWithdrawal command, PcAccount account)
        => new(command.Amount);
}

public static class PcReadAccountBalanceHandler
{
    public static decimal LastBalance { get; private set; }

    public static void Handle(PcReadAccountBalance query, [ReadModel] PcAccount account)
    {
        LastBalance = account.Balance;
    }
}
