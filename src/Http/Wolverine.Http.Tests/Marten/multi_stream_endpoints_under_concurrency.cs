using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

// GH-5625: the HTTP form of the multi-stream command handlers in MartenTests/AggregateHandlerWorkflow/MultiStream.
// An endpoint with an Async and an Inline aggregate fetched in one batch, many requests at once, with the async
// daemon moving the Async snapshots under the fetches.
public class multi_stream_endpoints_under_concurrency : IAsyncLifetime
{
    private IAlbaHost theHost = null!;
    private IDocumentStore theStore = null!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery();

            // Wolverine caches the detected application assembly process-wide, so name this one
            opts.Discovery.IncludeAssembly(typeof(multi_stream_endpoints_under_concurrency).Assembly);
        });

        builder.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "http_ms_5625";
                m.DisableNpgsqlLogging = true;

                m.Projections.Snapshot<HttpMsWallet>(SnapshotLifecycle.Async);
                m.Projections.Snapshot<HttpMsVault>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddWolverineHttp();

        // No cleaning: every test opens new streams, and wiping the events under a running daemon would leave its
        // high water mark ahead of the restarted sequence
        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a multi-stream endpoint", type => type != typeof(HttpMsEndpoints)))));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task moves_from_async_wallets_into_an_inline_vault_under_concurrency()
    {
        for (var round = 0; round < 3; round++)
        {
            var wallets = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
            var vault = Guid.NewGuid();
            await using (var session = theStore.LightweightSession())
            {
                foreach (var id in wallets) session.Events.StartStream<HttpMsWallet>(id, new HttpMsOpened(50));
                session.Events.StartStream<HttpMsVault>(vault, new HttpMsVaultOpened());
                await session.SaveChangesAsync(Token);
            }

            var random = new Random(85625 + round);
            var requests = Enumerable.Range(0, 90)
                .Select(_ => new HttpMsMove(wallets[random.Next(wallets.Length)], vault, random.Next(1, 10)))
                .ToArray();

            var applied = 0;
            var rejected = 0;
            var conflicts = 0;
            await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 12 },
                async (request, _) =>
                {
                    var result = await theHost.Scenario(x =>
                    {
                        x.Post.Json(request).ToUrl("/ms/move");
                        x.IgnoreStatusCode();
                    });

                    switch (result.Context.Response.StatusCode)
                    {
                        case 200:
                            var outcome = await result.ReadAsJsonAsync<HttpMsOutcome>();
                            if (outcome!.Applied) Interlocked.Increment(ref applied);
                            else Interlocked.Increment(ref rejected);
                            break;
                        case 409:
                            Interlocked.Increment(ref conflicts);
                            break;
                        default:
                            throw new Exception(
                                $"Unexpected {result.Context.Response.StatusCode}: {await result.ReadAsTextAsync()}");
                    }
                });

            (applied + rejected + conflicts).ShouldBe(requests.Length);
            applied.ShouldBeGreaterThan(0);

            await theStore.WaitForNonStaleProjectionDataAsync(60.Seconds());
            await using var query = theStore.LightweightSession();

            var total = 0m;
            foreach (var id in wallets)
            {
                var events = await query.Events.FetchStreamAsync(id, token: Token);
                (await query.Events.FetchStreamStateAsync(id, Token))!.Version.ShouldBe(events.Count);

                var live = (await query.Events.AggregateStreamAsync<HttpMsWallet>(id, token: Token))!;
                live.Balance.ShouldBeGreaterThanOrEqualTo(0);
                (await query.LoadAsync<HttpMsWallet>(id, Token))!.Balance.ShouldBe(live.Balance);
                total += live.Balance;
            }

            var vaultEvents = await query.Events.FetchStreamAsync(vault, token: Token);
            vaultEvents.Count.ShouldBe(1 + applied);
            var vaultBalance = (await query.LoadAsync<HttpMsVault>(vault, Token))!.Balance;
            vaultBalance.ShouldBe((await query.Events.AggregateStreamAsync<HttpMsVault>(vault, token: Token))!.Balance);

            (total + vaultBalance).ShouldBe(150);
        }
    }
}

public record HttpMsOpened(decimal Balance);

public record HttpMsWithdrawn(decimal Amount);

public record HttpMsVaultOpened;

public record HttpMsStored(decimal Amount);

public class HttpMsWallet
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static HttpMsWallet Create(HttpMsOpened e) => new() { Balance = e.Balance };
    public void Apply(HttpMsWithdrawn e) => Balance -= e.Amount;
}

public class HttpMsVault
{
    public Guid Id { get; set; }
    public decimal Balance { get; set; }

    public static HttpMsVault Create(HttpMsVaultOpened _) => new();
    public void Apply(HttpMsStored e) => Balance += e.Amount;
}

public record HttpMsMove(Guid WalletId, Guid VaultId, decimal Amount);

public record HttpMsOutcome(bool Applied);

public static class HttpMsEndpoints
{
    [WolverinePost("/ms/move")]
    public static HttpMsOutcome Post(HttpMsMove command,
        [WriteAggregate(nameof(HttpMsMove.WalletId))] IEventStream<HttpMsWallet> wallet,
        [WriteAggregate(nameof(HttpMsMove.VaultId))] IEventStream<HttpMsVault> vault)
    {
        if (wallet.Aggregate!.Balance < command.Amount) return new HttpMsOutcome(false);

        wallet.AppendOne(new HttpMsWithdrawn(command.Amount));
        vault.AppendOne(new HttpMsStored(command.Amount));
        return new HttpMsOutcome(true);
    }

    // A lost optimistic concurrency race is a 409, as in MartenConcurrencyExceptionMiddleware
    public static ProblemDetails OnException(ConcurrencyException ex)
        => new() { Status = 409, Title = "Conflict", Detail = ex.Message };
}
