using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events;
using JasperFx.Resources;
using Polecat;
using Polecat.Events;
using Polecat.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AggregateHandlerWorkflow;

public class multi_stream_version_and_consistency : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private Guid fromAccountId;
    private Guid toAccountId;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "multi_stream";
                        m.Projections.Snapshot<BankAccount>(SnapshotLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)theStore).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task GivenAccounts(decimal fromBalance = 1000, decimal toBalance = 500)
    {
        await using var session = theStore.LightweightSession();
        fromAccountId = session.Events.StartStream<BankAccount>(new BankAccountOpened(fromBalance)).Id;
        toAccountId = session.Events.StartStream<BankAccount>(new BankAccountOpened(toBalance)).Id;
        await session.SaveChangesAsync();
    }

    private async Task<BankAccount> LoadAccount(Guid id)
    {
        await using var session = theStore.LightweightSession();
        return await session.LoadAsync<BankAccount>(id);
    }

    [Fact]
    public async Task only_first_stream_gets_version_check_by_default()
    {
        await GivenAccounts();

        await theHost.InvokeMessageAndWaitAsync(
            new TransferFunds(fromAccountId, toAccountId, 100, Version: 1));

        var from = await LoadAccount(fromAccountId);
        var to = await LoadAccount(toAccountId);
        from.Balance.ShouldBe(900);
        to.Balance.ShouldBe(600);
    }

    [Fact]
    public async Task wrong_version_on_first_stream_causes_concurrency_error()
    {
        await GivenAccounts();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new TransferFunds(fromAccountId, toAccountId, 100, Version: 99)));
    }

    [Fact]
    public async Task explicit_version_source_on_secondary_stream()
    {
        await GivenAccounts();

        await theHost.InvokeMessageAndWaitAsync(
            new TransferFundsWithDualVersion(fromAccountId, toAccountId, 100,
                FromVersion: 1, ToVersion: 1));

        var from = await LoadAccount(fromAccountId);
        var to = await LoadAccount(toAccountId);
        from.Balance.ShouldBe(900);
        to.Balance.ShouldBe(600);
    }

    [Fact]
    public async Task wrong_version_on_explicitly_sourced_secondary_stream()
    {
        await GivenAccounts();

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new TransferFundsWithDualVersion(fromAccountId, toAccountId, 100,
                    FromVersion: 1, ToVersion: 99)));
    }

    [Fact]
    public async Task always_enforce_consistency_on_secondary_stream_with_no_events()
    {
        await GivenAccounts(fromBalance: 0);

        await Should.ThrowAsync<ConcurrencyException>(
            theHost.InvokeMessageAndWaitAsync(
                new TransferWithConsistencyCheck(fromAccountId, toAccountId, 100)));
    }

    [Fact]
    public async Task always_enforce_consistency_happy_path_no_events()
    {
        await GivenAccounts(fromBalance: 0);

        await theHost.InvokeMessageAndWaitAsync(
            new TransferWithConsistencyCheckNoConcurrentModification(fromAccountId, toAccountId, 100));

        var from = await LoadAccount(fromAccountId);
        var to = await LoadAccount(toAccountId);
        from.Balance.ShouldBe(0);
        to.Balance.ShouldBe(500);
    }
}

#region Aggregate and Events

public class BankAccount
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static BankAccount Create(BankAccountOpened opened) =>
        new() { Balance = opened.InitialBalance };

    public void Apply(FundsWithdrawn e) => Balance -= e.Amount;
    public void Apply(FundsDeposited e) => Balance += e.Amount;
}

public record BankAccountOpened(decimal InitialBalance);
public record FundsWithdrawn(decimal Amount);
public record FundsDeposited(decimal Amount);

#endregion

#region Commands
public record TransferFunds(Guid BankAccountId, Guid ToAccountId, decimal Amount, long Version);

public record TransferFundsWithDualVersion(
    Guid FromAccountId, Guid ToAccountId, decimal Amount,
    long FromVersion, long ToVersion);

public record TransferWithConsistencyCheck(Guid FromAccountId, Guid ToAccountId, decimal Amount);

public record TransferWithConsistencyCheckNoConcurrentModification(
    Guid FromAccountId, Guid ToAccountId, decimal Amount);

#endregion

#region Handlers
public static class TransferFundsHandler
{
    public static void Handle(
        TransferFunds command,
        [WriteAggregate] IEventStream<BankAccount> fromAccount,
        [WriteAggregate(nameof(TransferFunds.ToAccountId))] IEventStream<BankAccount> toAccount)
    {
        if (fromAccount.Aggregate!.Balance >= command.Amount)
        {
            fromAccount.AppendOne(new FundsWithdrawn(command.Amount));
            toAccount.AppendOne(new FundsDeposited(command.Amount));
        }
    }
}

public static class TransferFundsWithDualVersionHandler
{
    public static void Handle(
        TransferFundsWithDualVersion command,
        [WriteAggregate(nameof(TransferFundsWithDualVersion.FromAccountId),
            VersionSource = nameof(TransferFundsWithDualVersion.FromVersion))]
        IEventStream<BankAccount> fromAccount,
        [WriteAggregate(nameof(TransferFundsWithDualVersion.ToAccountId),
            VersionSource = nameof(TransferFundsWithDualVersion.ToVersion))]
        IEventStream<BankAccount> toAccount)
    {
        if (fromAccount.Aggregate!.Balance >= command.Amount)
        {
            fromAccount.AppendOne(new FundsWithdrawn(command.Amount));
            toAccount.AppendOne(new FundsDeposited(command.Amount));
        }
    }
}

public static class TransferWithConsistencyCheckHandler
{
    public static async Task Handle(
        TransferWithConsistencyCheck command,
        [WriteAggregate(nameof(TransferWithConsistencyCheck.FromAccountId),
            AlwaysEnforceConsistency = true)]
        IEventStream<BankAccount> fromAccount,
        [WriteAggregate(nameof(TransferWithConsistencyCheck.ToAccountId))]
        IEventStream<BankAccount> toAccount,
        IDocumentStore store)
    {
        await using var sneakySession = store.LightweightSession();
        sneakySession.Events.Append(command.FromAccountId, new FundsDeposited(1));
        await sneakySession.SaveChangesAsync();

        if (fromAccount.Aggregate!.Balance >= command.Amount)
        {
            fromAccount.AppendOne(new FundsWithdrawn(command.Amount));
            toAccount.AppendOne(new FundsDeposited(command.Amount));
        }
    }
}

public static class TransferWithConsistencyCheckNoConcurrentModificationHandler
{
    public static void Handle(
        TransferWithConsistencyCheckNoConcurrentModification command,
        [WriteAggregate(nameof(TransferWithConsistencyCheckNoConcurrentModification.FromAccountId),
            AlwaysEnforceConsistency = true)]
        IEventStream<BankAccount> fromAccount,
        [WriteAggregate(nameof(TransferWithConsistencyCheckNoConcurrentModification.ToAccountId))]
        IEventStream<BankAccount> toAccount)
    {
        if (fromAccount.Aggregate!.Balance >= command.Amount)
        {
            fromAccount.AppendOne(new FundsWithdrawn(command.Amount));
            toAccount.AppendOne(new FundsDeposited(command.Amount));
        }
    }
}

#endregion
