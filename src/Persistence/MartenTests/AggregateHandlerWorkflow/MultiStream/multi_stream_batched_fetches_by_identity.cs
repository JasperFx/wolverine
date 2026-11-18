using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.MultiTenancy;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StronglyTypedIds;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow.MultiStream;

// GH-5625: the concurrency tests of multi_stream_batched_fetches with string streams, strong-typed ids, natural keys
// and conjoined tenancy. Each store needs its own host. The async daemon runs in every one.

public class multi_stream_batched_fetches_with_string_streams : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await MsHosts.StartAsync(typeof(MsKeyedHandlers), "ms_batched_strings", m =>
        {
            m.Events.StreamIdentity = StreamIdentity.AsString;
            m.Projections.Snapshot<MsKeyedWallet>(SnapshotLifecycle.Async);
            m.Projections.Snapshot<MsKeyedVault>(SnapshotLifecycle.Inline);
        });

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task transfers_and_moves_between_string_streams_under_concurrency()
    {
        for (var round = 0; round < 3; round++)
        {
            var wallets = Enumerable.Range(0, 3).Select(_ => $"wallet-{Guid.NewGuid():N}").ToArray();
            var vault = $"vault-{Guid.NewGuid():N}";
            await using (var session = theStore.LightweightSession())
            {
                foreach (var key in wallets) session.Events.StartStream<MsKeyedWallet>(key, new MsOpened(100));
                session.Events.StartStream<MsKeyedVault>(vault, new MsVaultOpened());
                await session.SaveChangesAsync(Token);
            }

            var random = new Random(45625 + round);
            var commands = Enumerable.Range(0, 150).Select(i =>
            {
                var from = random.Next(wallets.Length);
                return i % 3 == 0
                    ? (object)new MsKeyedMove(wallets[from], vault, random.Next(1, 15))
                    : new MsKeyedTransfer(wallets[from], wallets[(from + 1) % wallets.Length], random.Next(1, 30));
            }).ToArray();

            var results = await MsHarness.InvokeConcurrentlyAsync(theHost, commands);
            results.Applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var query = theStore.LightweightSession();

            var total = 0m;
            foreach (var key in wallets)
            {
                var events = await query.Events.FetchStreamAsync(key, token: Token);
                MsHarness.StreamIsWhole(events, (await query.Events.FetchStreamStateAsync(key, Token))!, key);

                var live = (await query.Events.AggregateStreamAsync<MsKeyedWallet>(key, token: Token))!;
                live.Balance.ShouldBeGreaterThanOrEqualTo(0);
                (await query.LoadAsync<MsKeyedWallet>(key, Token))!.Balance.ShouldBe(live.Balance);
                (await query.Events.FetchLatest<MsKeyedWallet>(key, Token))!.Balance.ShouldBe(live.Balance);
                total += live.Balance;
            }

            var vaultEvents = await query.Events.FetchStreamAsync(vault, token: Token);
            MsHarness.StreamIsWhole(vaultEvents, (await query.Events.FetchStreamStateAsync(vault, Token))!, vault);
            var vaultBalance = (await query.LoadAsync<MsKeyedVault>(vault, Token))!.Balance;
            vaultBalance.ShouldBe((await query.Events.AggregateStreamAsync<MsKeyedVault>(vault, token: Token))!.Balance);

            (total + vaultBalance).ShouldBe(300);
        }
    }
}

public class multi_stream_batched_fetches_with_strong_typed_ids : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await MsHosts.StartAsync(typeof(MsStrongHandlers), "ms_batched_strong", m =>
        {
            m.Projections.Snapshot<MsStrongWallet>(SnapshotLifecycle.Async);
            m.Projections.Snapshot<MsStrongVault>(SnapshotLifecycle.Inline);
        });

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task transfers_and_moves_between_strong_typed_streams_under_concurrency()
    {
        for (var round = 0; round < 3; round++)
        {
            var wallets = Enumerable.Range(0, 3).Select(_ => MsWalletId.New()).ToArray();
            var vault = MsWalletId.New();
            await using (var session = theStore.LightweightSession())
            {
                foreach (var id in wallets) session.Events.StartStream<MsStrongWallet>(id.Value, new MsOpened(100));
                session.Events.StartStream<MsStrongVault>(vault.Value, new MsVaultOpened());
                await session.SaveChangesAsync(Token);
            }

            var random = new Random(55625 + round);
            var commands = Enumerable.Range(0, 150).Select(i =>
            {
                var from = random.Next(wallets.Length);
                return i % 3 == 0
                    ? (object)new MsStrongMove(wallets[from], vault, random.Next(1, 15))
                    : new MsStrongTransfer(wallets[from], wallets[(from + 1) % wallets.Length], random.Next(1, 30));
            }).ToArray();

            var results = await MsHarness.InvokeConcurrentlyAsync(theHost, commands);
            results.Applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var query = theStore.LightweightSession();

            var total = 0m;
            foreach (var id in wallets)
            {
                var events = await query.Events.FetchStreamAsync(id.Value, token: Token);
                MsHarness.StreamIsWhole(events, (await query.Events.FetchStreamStateAsync(id.Value, Token))!,
                    id.ToString());

                var live = (await query.Events.AggregateStreamAsync<MsStrongWallet>(id.Value, token: Token))!;
                live.Balance.ShouldBeGreaterThanOrEqualTo(0);
                (await query.LoadAsync<MsStrongWallet>(id, Token))!.Balance.ShouldBe(live.Balance);
                (await query.Events.FetchLatest<MsStrongWallet>(id.Value, Token))!.Balance.ShouldBe(live.Balance);
                total += live.Balance;
            }

            var vaultBalance = (await query.LoadAsync<MsStrongVault>(vault, Token))!.Balance;
            vaultBalance.ShouldBe(
                (await query.Events.AggregateStreamAsync<MsStrongVault>(vault.Value, token: Token))!.Balance);

            (total + vaultBalance).ShouldBe(300);
        }
    }
}

public class multi_stream_batched_fetches_with_natural_keys : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // The natural key aggregate is Inline: an Async natural-key fetch reads the snapshot only (marten#5799's
        // "not in this PR"), which is a known gap rather than something to pin here
        theHost = await MsHosts.StartAsync(typeof(MsNaturalKeyHandlers), "ms_batched_nk", m =>
        {
            m.Projections.Snapshot<MsNkAccount>(SnapshotLifecycle.Inline);
            m.Projections.Snapshot<MsWallet>(SnapshotLifecycle.Async);
        });

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task payments_from_natural_key_accounts_into_async_wallets_under_concurrency()
    {
        for (var round = 0; round < 3; round++)
        {
            // New account numbers every round, because a natural key identifies one stream for good
            var accounts = Enumerable.Range(0, 2)
                .Select(_ => (Id: Guid.NewGuid(), Number: new MsAccountNumber($"ACC-{Guid.NewGuid():N}")))
                .ToArray();
            var wallets = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToArray();

            await using (var session = theStore.LightweightSession())
            {
                foreach (var account in accounts)
                {
                    session.Events.StartStream<MsNkAccount>(account.Id, new MsNkAccountOpened(account.Number, 200));
                }

                foreach (var wallet in wallets) session.Events.StartStream<MsWallet>(wallet, new MsOpened(0));
                await session.SaveChangesAsync(Token);
            }

            var random = new Random(65625 + round);
            var commands = Enumerable.Range(0, 120)
                .Select(_ => (object)new MsPayFromAccount(accounts[random.Next(accounts.Length)].Number,
                    wallets[random.Next(wallets.Length)], random.Next(1, 20)))
                .ToArray();

            var results = await MsHarness.InvokeConcurrentlyAsync(theHost, commands);
            results.Applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var query = theStore.LightweightSession();

            var total = 0m;
            var payments = 0;
            foreach (var account in accounts)
            {
                var events = await query.Events.FetchStreamAsync(account.Id, token: Token);
                MsHarness.StreamIsWhole(events, (await query.Events.FetchStreamStateAsync(account.Id, Token))!,
                    account.Number.Value);

                var live = (await query.Events.AggregateStreamAsync<MsNkAccount>(account.Id, token: Token))!;
                live.Balance.ShouldBeGreaterThanOrEqualTo(0);
                (await query.LoadAsync<MsNkAccount>(account.Id, Token))!.Balance.ShouldBe(live.Balance);
                total += live.Balance;
                payments += events.Count(x => x.Data is MsWithdrawn);
            }

            foreach (var wallet in wallets)
            {
                var events = await query.Events.FetchStreamAsync(wallet, token: Token);
                MsHarness.StreamIsWhole(events, (await query.Events.FetchStreamStateAsync(wallet, Token))!,
                    wallet.ToString());

                var live = (await query.Events.AggregateStreamAsync<MsWallet>(wallet, token: Token))!;
                (await query.LoadAsync<MsWallet>(wallet, Token))!.Balance.ShouldBe(live.Balance);
                total += live.Balance;
            }

            total.ShouldBe(400);
            payments.ShouldBe(results.Applied);
        }
    }
}

public class multi_stream_batched_fetches_with_conjoined_tenancy : IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await MsHosts.StartAsync(typeof(MsWalletHandlers), "ms_batched_tenanted", m =>
        {
            m.Events.TenancyStyle = TenancyStyle.Conjoined;
            m.Policies.AllDocumentsAreMultiTenanted();
            m.Projections.Snapshot<MsWallet>(SnapshotLifecycle.Async);
            m.Projections.Snapshot<MsVault>(SnapshotLifecycle.Inline);
        });

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task the_same_streams_in_two_tenants_stay_apart_under_concurrency()
    {
        for (var round = 0; round < 3; round++)
        {
            // The same stream ids in both tenants
            var wallets = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
            var vault = Guid.NewGuid();
            var tally = Guid.NewGuid();
            var balances = new Dictionary<string, decimal> { ["red"] = 100, ["blue"] = 40 };

            foreach (var (tenant, balance) in balances)
            {
                await using var session = theStore.LightweightSession(tenant);
                foreach (var id in wallets) session.Events.StartStream<MsWallet>(id, new MsOpened(balance));
                session.Events.StartStream<MsVault>(vault, new MsVaultOpened());
                session.Events.StartStream<MsTally>(tally, new MsTallyStarted());
                await session.SaveChangesAsync(Token);
            }

            var random = new Random(75625 + round);
            var commands = Enumerable.Range(0, 90).Select(i =>
            {
                var from = random.Next(wallets.Length);
                return i % 3 == 0
                    ? (object)new MsMove(wallets[from], vault, tally, random.Next(1, 10))
                    : new MsTransfer(wallets[from], wallets[(from + 1) % wallets.Length], random.Next(1, 30));
            }).ToArray();

            var red = MsHarness.InvokeConcurrentlyAsync(theHost, commands, "red");
            var blue = MsHarness.InvokeConcurrentlyAsync(theHost, commands, "blue");
            var results = new Dictionary<string, MsResults> { ["red"] = await red, ["blue"] = await blue };

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());

            foreach (var (tenant, balance) in balances)
            {
                await using var query = theStore.LightweightSession(tenant);

                var total = 0m;
                foreach (var id in wallets)
                {
                    var events = await query.Events.FetchStreamAsync(id, token: Token);
                    MsHarness.StreamIsWhole(events, (await query.Events.FetchStreamStateAsync(id, Token))!,
                        $"{tenant}/{id}");

                    var live = (await query.Events.AggregateStreamAsync<MsWallet>(id, token: Token))!;
                    live.Balance.ShouldBeGreaterThanOrEqualTo(0);
                    (await query.LoadAsync<MsWallet>(id, Token))!.Balance.ShouldBe(live.Balance, $"{tenant}/{id}");
                    (await query.Events.FetchLatest<MsWallet>(id, Token))!.Balance.ShouldBe(live.Balance);
                    total += live.Balance;
                }

                var vaultBalance = (await query.LoadAsync<MsVault>(vault, Token))!.Balance;
                var tallyCount = (await query.Events.FetchLatest<MsTally>(tally, Token))!.Count;
                vaultBalance.ShouldBe((await query.Events.AggregateStreamAsync<MsVault>(vault, token: Token))!.Balance);

                // Each tenant holds its own money and only its own moves
                (total + vaultBalance).ShouldBe(3 * balance, tenant);
                var moves = (await query.Events.FetchStreamAsync(vault, token: Token)).Count - 1;
                tallyCount.ShouldBe(moves, tenant);
                results[tenant].Applied.ShouldBeGreaterThanOrEqualTo(moves);
            }
        }
    }
}

internal static class MsHosts
{
    public static Task<IHost> StartAsync(Type handlers, string schema, Action<StoreOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlers);
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = schema;
                        m.DisableNpgsqlLogging = true;
                        configure(m);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }
}

#region String streams

public class MsKeyedWallet
{
    public string Id { get; set; } = null!;
    public decimal Balance { get; set; }

    public static MsKeyedWallet Create(MsOpened e) => new() { Balance = e.Balance };
    public void Apply(MsDeposited e) => Balance += e.Amount;
    public void Apply(MsWithdrawn e) => Balance -= e.Amount;
}

public class MsKeyedVault
{
    public string Id { get; set; } = null!;
    public decimal Balance { get; set; }

    public static MsKeyedVault Create(MsVaultOpened _) => new();
    public void Apply(MsStored e) => Balance += e.Amount;
}

public record MsKeyedTransfer(string FromKey, string ToKey, decimal Amount);

public record MsKeyedMove(string WalletKey, string VaultKey, decimal Amount);

public static class MsKeyedHandlers
{
    public static MsOutcome Handle(MsKeyedTransfer command,
        [WriteAggregate(nameof(MsKeyedTransfer.FromKey))] IEventStream<MsKeyedWallet> from,
        [WriteAggregate(nameof(MsKeyedTransfer.ToKey))] IEventStream<MsKeyedWallet> to)
    {
        if (from.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        from.AppendOne(new MsWithdrawn(command.Amount));
        to.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }

    public static MsOutcome Handle(MsKeyedMove command,
        [WriteAggregate(nameof(MsKeyedMove.WalletKey))] IEventStream<MsKeyedWallet> wallet,
        [WriteAggregate(nameof(MsKeyedMove.VaultKey))] IEventStream<MsKeyedVault> vault)
    {
        if (wallet.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        wallet.AppendOne(new MsWithdrawn(command.Amount));
        vault.AppendOne(new MsStored(command.Amount));
        return new MsOutcome(true, command.Amount);
    }
}

#endregion

#region Strong-typed ids

[StronglyTypedId(Template.Guid)]
public readonly partial struct MsWalletId;

public class MsStrongWallet
{
    public MsWalletId Id { get; set; }
    public decimal Balance { get; set; }

    public static MsStrongWallet Create(MsOpened e) => new() { Balance = e.Balance };
    public void Apply(MsDeposited e) => Balance += e.Amount;
    public void Apply(MsWithdrawn e) => Balance -= e.Amount;
}

public class MsStrongVault
{
    public MsWalletId Id { get; set; }
    public decimal Balance { get; set; }

    public static MsStrongVault Create(MsVaultOpened _) => new();
    public void Apply(MsStored e) => Balance += e.Amount;
}

public record MsStrongTransfer(MsWalletId FromId, MsWalletId ToId, decimal Amount);

public record MsStrongMove(MsWalletId WalletId, MsWalletId VaultId, decimal Amount);

public static class MsStrongHandlers
{
    public static MsOutcome Handle(MsStrongTransfer command,
        [WriteAggregate(nameof(MsStrongTransfer.FromId))] IEventStream<MsStrongWallet> from,
        [WriteAggregate(nameof(MsStrongTransfer.ToId))] IEventStream<MsStrongWallet> to)
    {
        if (from.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        from.AppendOne(new MsWithdrawn(command.Amount));
        to.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }

    public static MsOutcome Handle(MsStrongMove command,
        [WriteAggregate(nameof(MsStrongMove.WalletId))] IEventStream<MsStrongWallet> wallet,
        [WriteAggregate(nameof(MsStrongMove.VaultId))] IEventStream<MsStrongVault> vault)
    {
        if (wallet.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        wallet.AppendOne(new MsWithdrawn(command.Amount));
        vault.AppendOne(new MsStored(command.Amount));
        return new MsOutcome(true, command.Amount);
    }
}

#endregion

#region Natural keys

public record MsAccountNumber(string Value);

public record MsNkAccountOpened(MsAccountNumber Number, decimal Balance);

public class MsNkAccount
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MsAccountNumber Number { get; set; } = null!;

    public decimal Balance { get; set; }

    [NaturalKeySource]
    public void Apply(MsNkAccountOpened e)
    {
        Number = e.Number;
        Balance = e.Balance;
    }

    public void Apply(MsWithdrawn e) => Balance -= e.Amount;
}

public record MsPayFromAccount(MsAccountNumber Number, Guid WalletId, decimal Amount);

public static class MsNaturalKeyHandlers
{
    public static MsOutcome Handle(MsPayFromAccount command,
        [WriteAggregate(nameof(MsPayFromAccount.Number))] IEventStream<MsNkAccount> account,
        [WriteAggregate(nameof(MsPayFromAccount.WalletId))] IEventStream<MsWallet> wallet)
    {
        if (account.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        account.AppendOne(new MsWithdrawn(command.Amount));
        wallet.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }
}

#endregion
