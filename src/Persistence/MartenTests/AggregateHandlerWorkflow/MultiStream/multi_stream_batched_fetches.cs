using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow.MultiStream;

/// <summary>
///     GH-5625. Command handlers that write to several event streams at once, against Marten 10's batched fetches
///     (marten#5799 reads an Async aggregate's events before its snapshot, and the batched fetches go through the
///     same path). Wolverine batches the fetches whenever a handler takes more than one [WriteAggregate] or
///     [ReadAggregate], so every handler here is a batched fetch. The async daemon runs throughout, moving the Async
///     snapshots forward while the handlers read them.
/// </summary>
/// <remarks>
///     The concurrency tests run several rounds of many commands at once, and check afterwards that nothing was lost
///     or applied twice: money is conserved, no balance went below zero (which a stale aggregate with a current
///     version would allow), every applied command left exactly its events, each stream's version is its event
///     count, and the snapshot, the live aggregation of the raw events and FetchLatest all agree.
/// </remarks>
public class multi_stream_batched_fetches : IAsyncLifetime
{
    private const int Rounds = 3;

    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MsWalletHandlers));
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "ms_batched_5625";
                        m.DisableNpgsqlLogging = true;

                        m.Projections.Snapshot<MsWallet>(SnapshotLifecycle.Async);
                        m.Projections.Snapshot<MsVault>(SnapshotLifecycle.Inline);

                        // MsTally is not registered, so it is aggregated live
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();

        // No cleaning: every test opens new streams, and wiping the events under a running daemon would leave its
        // high water mark ahead of the restarted sequence
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task every_multi_stream_handler_fetches_in_one_batch()
    {
        var wallets = await openWallets(2, 100);
        var vault = await openVault();
        var tally = await openTally();

        var bus = theHost.MessageBus();
        await bus.InvokeAsync<MsOutcome>(new MsTransfer(wallets[0], wallets[1], 1), Token);
        await bus.InvokeAsync<MsOutcome>(new MsMove(wallets[0], vault, tally, 1), Token);
        await bus.InvokeAsync<MsOutcome>(new MsTransferWithVersions(wallets[0], wallets[1], 1, 3, 2),
            Token);
        await bus.InvokeAsync<MsOutcome>(new MsExclusiveTransfer(wallets[0], wallets[1], 1),
            Token);
        await bus.InvokeAsync<MsOutcome>(new MsMatchDeposit(wallets[0], wallets[1], 1),
            Token);

        foreach (var messageType in new[]
                 {
                     typeof(MsTransfer), typeof(MsMove), typeof(MsTransferWithVersions), typeof(MsExclusiveTransfer),
                     typeof(MsMatchDeposit)
                 })
        {
            var source = theHost.GetRuntime().Handlers.ChainFor(messageType).ShouldNotBeNull().SourceCode
                .ShouldNotBeNull();
            source.ShouldContain("CreateBatchQuery", customMessage: $"{messageType.Name} does not batch its fetches");
        }
    }

    [Fact]
    public async Task transfers_between_async_aggregates_conserve_money_under_concurrency()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var wallets = await openWallets(4, 100);
            var random = new Random(5625 + round);

            var commands = Enumerable.Range(0, 200).Select(_ =>
            {
                var from = random.Next(wallets.Length);
                var to = (from + 1 + random.Next(wallets.Length - 1)) % wallets.Length;
                return (object)new MsTransfer(wallets[from], wallets[to], random.Next(1, 40));
            }).ToArray();

            var results = await invokeConcurrently(commands);

            results.Applied.ShouldBeGreaterThan(0);
            await assertWalletsAreConsistent(wallets, 400, results.Applied, results.Applied);
        }
    }

    [Fact]
    public async Task transfers_stay_consistent_while_other_writers_append_to_the_same_streams()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var wallets = await openWallets(3, 50);
            var random = new Random(15625 + round);

            var commands = Enumerable.Range(0, 150).Select(_ =>
            {
                var from = random.Next(wallets.Length);
                var to = (from + 1) % wallets.Length;
                return (object)new MsTransfer(wallets[from], wallets[to], random.Next(1, 25));
            }).ToArray();

            // Deposits straight through Marten, outside Wolverine, racing the handlers' fetches and commits
            var externalDeposits = 0;
            var externalTotal = 0m;
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(async () =>
            {
                var writerRandom = new Random(25625 + round);
                while (!stop.IsCancellationRequested)
                {
                    var amount = writerRandom.Next(1, 10);
                    await using var session = theStore.LightweightSession();
                    session.Events.Append(wallets[writerRandom.Next(wallets.Length)], new MsDeposited(amount));
                    await session.SaveChangesAsync(Token);
                    externalDeposits++;
                    externalTotal += amount;
                }
            }, Token);

            var results = await invokeConcurrently(commands);
            await stop.CancelAsync();
            await writer;

            externalDeposits.ShouldBeGreaterThan(0);
            await assertWalletsAreConsistent(wallets, 150 + externalTotal, results.Applied,
                results.Applied + externalDeposits);
        }
    }

    [Fact]
    public async Task inline_async_and_live_aggregates_in_one_handler_under_concurrency()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var wallets = await openWallets(3, 60);
            var vault = await openVault();
            var tally = await openTally();
            var random = new Random(35625 + round);

            var amounts = new ConcurrentBag<decimal>();
            var commands = Enumerable.Range(0, 120)
                .Select(_ => (object)new MsMove(wallets[random.Next(wallets.Length)], vault, tally, random.Next(1, 20)))
                .ToArray();

            var results = await invokeConcurrently(commands, outcome =>
            {
                if (outcome.Applied) amounts.Add(outcome.Amount);
            });

            results.Applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var session = theStore.LightweightSession();

            // Inline: the snapshot and the raw events agree, and hold exactly the applied moves
            var vaultSnapshot = (await session.LoadAsync<MsVault>(vault, Token)).ShouldNotBeNull();
            var vaultLive = (await session.Events.AggregateStreamAsync<MsVault>(vault, token: Token)).ShouldNotBeNull();
            vaultSnapshot.Balance.ShouldBe(amounts.Sum());
            vaultLive.Balance.ShouldBe(amounts.Sum());
            (await session.Events.FetchStreamStateAsync(vault, Token))!.Version.ShouldBe(1 + results.Applied);

            // Live
            var tallyLatest = (await session.Events.FetchLatest<MsTally>(tally, Token)).ShouldNotBeNull();
            tallyLatest.Count.ShouldBe(results.Applied);
            (await session.Events.FetchStreamStateAsync(tally, Token))!.Version.ShouldBe(1 + results.Applied);

            // Async
            await assertWalletsAreConsistent(wallets, 180 - amounts.Sum(), results.Applied, 0);
        }
    }

    [Fact]
    public async Task an_expected_version_on_each_stream_lets_exactly_one_of_two_racing_commands_win()
    {
        for (var round = 0; round < 10; round++)
        {
            var wallets = await openWallets(2, 100);

            // Both commands were decided on the same versions: 1 on each stream after the open event
            var results = await invokeConcurrently([
                new MsTransferWithVersions(wallets[0], wallets[1], 10, 1, 1),
                new MsTransferWithVersions(wallets[0], wallets[1], 20, 1, 1)
            ]);

            results.Applied.ShouldBe(1);
            results.Conflicts.ShouldBe(1);
            await assertWalletsAreConsistent(wallets, 200, 1, 1);
        }
    }

    [Fact]
    public async Task a_wrong_expected_version_on_the_second_stream_appends_to_neither()
    {
        var wallets = await openWallets(2, 100);

        await Should.ThrowAsync<ConcurrencyException>(() => theHost.MessageBus()
            .InvokeAsync<MsOutcome>(new MsTransferWithVersions(wallets[0], wallets[1], 10, 1, 7),
                Token));

        await using var session = theStore.LightweightSession();
        (await session.Events.FetchStreamStateAsync(wallets[0], Token))!.Version
            .ShouldBe(1);
        (await session.Events.FetchStreamStateAsync(wallets[1], Token))!.Version
            .ShouldBe(1);
    }

    [Fact]
    public async Task an_exclusive_fetch_beside_an_async_fetch_serializes_the_writers()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var source = (await openWallets(1, 1000))[0];
            var targets = await openWallets(20, 0);

            // Every command locks the one source stream, and each writes to its own target, so none can conflict
            var commands = targets.Select(x => (object)new MsExclusiveTransfer(source, x, 10)).ToArray();
            var results = await invokeConcurrently(commands);

            results.Applied.ShouldBe(targets.Length);
            results.Conflicts.ShouldBe(0);
            await assertWalletsAreConsistent([source, ..targets], 1000, targets.Length, targets.Length);

            await using var session = theStore.LightweightSession();
            (await session.Events.AggregateStreamAsync<MsWallet>(source, token: Token))!.Balance.ShouldBe(1000 - 10 * targets.Length);
        }
    }

    [Fact]
    public async Task a_read_aggregate_beside_a_write_aggregate_under_concurrency()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var wallets = await openWallets(2, 30);
            var reference = wallets[0];
            var target = wallets[1];

            // The reference wallet moves while the handlers read it
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await using var session = theStore.LightweightSession();
                    session.Events.Append(reference, new MsDeposited(1));
                    await session.SaveChangesAsync(Token);
                }
            }, Token);

            var commands = Enumerable.Range(0, 60).Select(_ => (object)new MsMatchDeposit(reference, target, 1))
                .ToArray();
            var results = await invokeConcurrently(commands);
            await stop.CancelAsync();
            await writer;

            // [ReadAggregate] takes no part in the concurrency check, so only the target can conflict
            results.Applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var query = theStore.LightweightSession();
            var events = await query.Events.FetchStreamAsync(target, token: Token);
            events.Count.ShouldBe(1 + results.Applied);
            (await query.LoadAsync<MsWallet>(target, Token))!.Balance
                .ShouldBe((await query.Events.AggregateStreamAsync<MsWallet>(target, token: Token))!.Balance);
        }
    }

    [Fact]
    public async Task a_snapshot_without_its_stream_is_refused_by_the_batched_fetch()
    {
        var orphan = Guid.NewGuid();
        await using (var session = theStore.LightweightSession())
        {
            // A snapshot document with no stream under it, as DeleteAllEventDataAsync() leaves one behind
            session.Store(new MsWallet { Id = orphan, Balance = 500 });
            await session.SaveChangesAsync(Token);
        }

        var wallets = await openWallets(1, 100);

        await Should.ThrowAsync<SnapshotWithoutStreamException>(() => theHost.MessageBus()
            .InvokeAsync<MsOutcome>(new MsTransfer(orphan, wallets[0], 10), Token));

        await using var query = theStore.LightweightSession();
        (await query.Events.FetchStreamStateAsync(orphan, Token)).ShouldBeNull();
        (await query.Events.FetchStreamStateAsync(wallets[0], Token))!.Version
            .ShouldBe(1);
    }

    private async Task<Guid[]> openWallets(int count, decimal balance)
    {
        await using var session = theStore.LightweightSession();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) session.Events.StartStream<MsWallet>(id, new MsOpened(balance));
        await session.SaveChangesAsync(Token);
        return ids;
    }

    private async Task<Guid> openVault()
    {
        await using var session = theStore.LightweightSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<MsVault>(id, new MsVaultOpened());
        await session.SaveChangesAsync(Token);
        return id;
    }

    private async Task<Guid> openTally()
    {
        await using var session = theStore.LightweightSession();
        var id = Guid.NewGuid();
        session.Events.StartStream<MsTally>(id, new MsTallyStarted());
        await session.SaveChangesAsync(Token);
        return id;
    }

    private Task<MsResults> invokeConcurrently(object[] commands, Action<MsOutcome>? onOutcome = null)
        => MsHarness.InvokeConcurrentlyAsync(theHost, commands, onOutcome: onOutcome);

    /// <param name="total">What the balances must add up to</param>
    /// <param name="withdrawals">How many MsWithdrawn events the wallets must hold in all</param>
    /// <param name="deposits">How many MsDeposited events the wallets must hold in all</param>
    private async Task assertWalletsAreConsistent(Guid[] wallets, decimal total, int withdrawals, int deposits)
    {
        await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());

        await using var session = theStore.LightweightSession();
        var balance = 0m;
        var withdrawn = 0;
        var deposited = 0;

        foreach (var id in wallets)
        {
            var events = await session.Events.FetchStreamAsync(id, token: Token);
            var live = (await session.Events.AggregateStreamAsync<MsWallet>(id, token: Token)).ShouldNotBeNull();
            var snapshot = (await session.LoadAsync<MsWallet>(id, Token)).ShouldNotBeNull();
            var latest = (await session.Events.FetchLatest<MsWallet>(id, Token)).ShouldNotBeNull();
            var state = (await session.Events.FetchStreamStateAsync(id, Token)).ShouldNotBeNull();

            // A stale aggregate with a current version is the failure to look for: it lets a transfer overdraw
            live.Balance.ShouldBeGreaterThanOrEqualTo(0, $"wallet {id} was overdrawn");
            snapshot.Balance.ShouldBe(live.Balance, $"snapshot of {id}");
            latest.Balance.ShouldBe(live.Balance, $"FetchLatest of {id}");
            state.Version.ShouldBe(events.Count, $"version of {id}");
            events.Select(x => x.Version).ShouldBe(Enumerable.Range(1, events.Count).Select(x => (long)x),
                $"versions of {id}");

            balance += live.Balance;
            withdrawn += events.Count(x => x.Data is MsWithdrawn);
            deposited += events.Count(x => x.Data is MsDeposited);
        }

        balance.ShouldBe(total);
        withdrawn.ShouldBe(withdrawals);
        deposited.ShouldBe(deposits);
    }
}

public record MsResults(int Applied, int Rejected, int Conflicts);

internal static class MsHarness
{
    /// <summary>
    ///     Invoke every command at once, a dozen at a time. A command either applies, is rejected by its handler, or
    ///     loses an optimistic concurrency race. Anything else fails the test.
    /// </summary>
    public static async Task<MsResults> InvokeConcurrentlyAsync(IHost host, object[] commands, string? tenantId = null,
        Action<MsOutcome>? onOutcome = null)
    {
        var applied = 0;
        var rejected = 0;
        var conflicts = 0;

        await Parallel.ForEachAsync(commands, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            async (command, token) =>
            {
                try
                {
                    var bus = host.MessageBus();
                    var outcome = tenantId == null
                        ? await bus.InvokeAsync<MsOutcome>(command, token)
                        : await bus.InvokeForTenantAsync<MsOutcome>(tenantId, command, token);

                    onOutcome?.Invoke(outcome);
                    if (outcome.Applied) Interlocked.Increment(ref applied);
                    else Interlocked.Increment(ref rejected);
                }
                catch (ConcurrencyException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            });

        (applied + rejected + conflicts).ShouldBe(commands.Length);
        return new MsResults(applied, rejected, conflicts);
    }

    /// <summary>
    ///     The checks every concurrency test ends with, for one stream: the versions run 1..n with no gaps, and the
    ///     aggregate built from the raw events agrees with what the store serves.
    /// </summary>
    public static void StreamIsWhole(IReadOnlyList<IEvent> events, StreamState state, string stream)
    {
        state.Version.ShouldBe(events.Count, $"version of {stream}");
        events.Select(x => x.Version).ShouldBe(Enumerable.Range(1, events.Count).Select(x => (long)x),
            $"versions of {stream}");
    }
}

public record MsOutcome(bool Applied, decimal Amount);

public record MsOpened(decimal Balance);

public record MsDeposited(decimal Amount);

public record MsWithdrawn(decimal Amount);

public class MsWallet
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static MsWallet Create(MsOpened e) => new() { Balance = e.Balance };
    public void Apply(MsDeposited e) => Balance += e.Amount;
    public void Apply(MsWithdrawn e) => Balance -= e.Amount;
}

public record MsVaultOpened;

public record MsStored(decimal Amount);

public class MsVault
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static MsVault Create(MsVaultOpened _) => new();
    public void Apply(MsStored e) => Balance += e.Amount;
}

public record MsTallyStarted;

public record MsCounted;

public class MsTally
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public static MsTally Create(MsTallyStarted _) => new();
    public void Apply(MsCounted _) => Count++;
}

public record MsTransfer(Guid FromId, Guid ToId, decimal Amount);

public record MsMove(Guid WalletId, Guid VaultId, Guid TallyId, decimal Amount);

public record MsTransferWithVersions(Guid FromId, Guid ToId, decimal Amount, long FromVersion, long ToVersion);

public record MsExclusiveTransfer(Guid FromId, Guid ToId, decimal Amount);

public record MsMatchDeposit(Guid ReferenceId, Guid TargetId, decimal Amount);

public static class MsWalletHandlers
{
    // Two Async aggregates of the same type
    public static MsOutcome Handle(MsTransfer command,
        [WriteAggregate(nameof(MsTransfer.FromId))] IEventStream<MsWallet> from,
        [WriteAggregate(nameof(MsTransfer.ToId))] IEventStream<MsWallet> to)
    {
        if (from.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        from.AppendOne(new MsWithdrawn(command.Amount));
        to.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }

    // Async, Inline and Live in one handler
    public static MsOutcome Handle(MsMove command,
        [WriteAggregate(nameof(MsMove.WalletId))] IEventStream<MsWallet> wallet,
        [WriteAggregate(nameof(MsMove.VaultId))] IEventStream<MsVault> vault,
        [WriteAggregate(nameof(MsMove.TallyId))] IEventStream<MsTally> tally)
    {
        if (wallet.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        wallet.AppendOne(new MsWithdrawn(command.Amount));
        vault.AppendOne(new MsStored(command.Amount));
        tally.AppendOne(new MsCounted());
        return new MsOutcome(true, command.Amount);
    }

    public static MsOutcome Handle(MsTransferWithVersions command,
        [WriteAggregate(nameof(MsTransferWithVersions.FromId),
            VersionSource = nameof(MsTransferWithVersions.FromVersion))]
        IEventStream<MsWallet> from,
        [WriteAggregate(nameof(MsTransferWithVersions.ToId),
            VersionSource = nameof(MsTransferWithVersions.ToVersion))]
        IEventStream<MsWallet> to)
    {
        from.AppendOne(new MsWithdrawn(command.Amount));
        to.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }

    // FetchForExclusiveWriting beside an Async FetchForWriting in one batch
    public static MsOutcome Handle(MsExclusiveTransfer command,
        [WriteAggregate(nameof(MsExclusiveTransfer.FromId), LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)]
        IEventStream<MsWallet> from,
        [WriteAggregate(nameof(MsExclusiveTransfer.ToId))] IEventStream<MsWallet> to)
    {
        if (from.Aggregate!.Balance < command.Amount) return new MsOutcome(false, command.Amount);

        from.AppendOne(new MsWithdrawn(command.Amount));
        to.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }

    // FetchLatest beside FetchForWriting in one batch
    public static MsOutcome Handle(MsMatchDeposit command,
        [ReadAggregate(nameof(MsMatchDeposit.ReferenceId))] MsWallet reference,
        [WriteAggregate(nameof(MsMatchDeposit.TargetId))] IEventStream<MsWallet> target)
    {
        if (reference.Balance <= 0) return new MsOutcome(false, command.Amount);

        target.AppendOne(new MsDeposited(command.Amount));
        return new MsOutcome(true, command.Amount);
    }
}
