using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.ClaimCheck.Internal;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// GH-3509: the background sweeper that deletes aged claim-check payloads. It is registered only when
/// a time to live was configured, and it sweeps every configured store, not just the default one.
/// </summary>
public class claim_check_sweeper
{
    private static async Task<IHost> hostFor(Action<ClaimCheckConfiguration> configure)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(claim_check_sweeper).Assembly;
                opts.UseClaimCheck(configure);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task no_sweeper_is_registered_without_a_configured_ttl()
    {
        using var host = await hostFor(c => c.Store = new ExpiringInMemoryClaimCheckStore());

        // Default behavior must be unchanged: Wolverine never deletes a payload unless asked to.
        host.Services.GetServices<IHostedService>().OfType<ClaimCheckSweeper>().ShouldBeEmpty();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_sweeper_is_registered_when_a_ttl_is_configured()
    {
        using var host = await hostFor(c =>
        {
            c.Store = new ExpiringInMemoryClaimCheckStore();
            c.DeletePayloadsOlderThan(1.Hours());
        });

        host.Services.GetServices<IHostedService>().OfType<ClaimCheckSweeper>().ShouldHaveSingleItem();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task calling_use_claim_check_twice_registers_exactly_one_sweeper()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(claim_check_sweeper).Assembly;
                opts.UseClaimCheck(c =>
                {
                    c.Store = new ExpiringInMemoryClaimCheckStore();
                    c.DeletePayloadsOlderThan(1.Hours());
                });
                opts.UseClaimCheck(c =>
                {
                    c.Store = new ExpiringInMemoryClaimCheckStore();
                    c.DeletePayloadsOlderThan(2.Hours());
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetServices<IHostedService>().OfType<ClaimCheckSweeper>().ShouldHaveSingleItem();
        host.Services.GetServices<ClaimCheckSweepSettings>().ShouldHaveSingleItem()
            .TimeToLive.ShouldBe(2.Hours());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task sweeps_aged_payloads_out_of_every_configured_store()
    {
        var defaultStore = new ExpiringInMemoryClaimCheckStore();
        var routedStore = new ExpiringInMemoryClaimCheckStore();

        var stale = await defaultStore.StoreAsync(new byte[] { 1 }, "text/plain", TestContext.Current.CancellationToken);
        var staleRouted = await routedStore.StoreAsync(new byte[] { 2 }, "text/plain", TestContext.Current.CancellationToken);
        var fresh = await defaultStore.StoreAsync(new byte[] { 3 }, "text/plain", TestContext.Current.CancellationToken);

        defaultStore.Age(stale.Id, 10.Hours());
        routedStore.Age(staleRouted.Id, 10.Hours());

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(claim_check_sweeper).Assembly;
                opts.UseClaimCheck(c =>
                {
                    c.Store = defaultStore;
                    c.StoreForMessage<BlobStringMessage>(routedStore);
                    c.DeletePayloadsOlderThan(1.Hours());
                    c.SweepInterval = 1.Seconds();
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await waitFor(() => defaultStore.Count == 1 && routedStore.Count == 0);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // The routed store must be swept too -- a multi-store configuration would otherwise leak
        // everything that was not sent through the default backend.
        routedStore.Count.ShouldBe(0);

        defaultStore.Count.ShouldBe(1);
        defaultStore.Ids.ShouldContain(fresh.Id);
    }

    [Fact]
    public async Task a_store_without_expiration_support_is_skipped_without_throwing()
    {
        var unsupported = new RecordingInMemoryClaimCheckStore();

        using var host = await hostFor(c =>
        {
            c.Store = unsupported;
            c.DeletePayloadsOlderThan(1.Hours());
            c.SweepInterval = 1.Seconds();
        });

        // Give the sweeper time to wake, notice the backend cannot be swept, log, and carry on.
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        host.Services.GetServices<IHostedService>().OfType<ClaimCheckSweeper>().ShouldHaveSingleItem();
        unsupported.DeleteCount.ShouldBe(0);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task waitFor(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The claim-check sweeper did not reach the expected state in time.");
    }
}
