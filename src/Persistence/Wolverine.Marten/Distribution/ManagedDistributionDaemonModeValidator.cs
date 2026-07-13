using JasperFx.Events.Daemon;
using Marten;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Marten.Distribution;

/// <summary>
/// GH-3388: refuses a store that combines Wolverine-managed event subscription distribution with an
/// explicit Marten-side daemon (<c>MartenDaemonModeIsSolo()</c> / <c>AddAsyncDaemon(Solo|HotCold)</c>).
///
/// That combination is never valid. Marten hosts its own <c>ProjectionCoordinator</c> — an
/// <c>IHostedService</c> that starts the shards — while Wolverine's distribution drives the same
/// daemon through its own coordinator. The two compete, and the symptom is not an error but a HANG:
/// a tracked catch-up that never completes. It cost a user a day to find, and nothing in the code
/// that stalls points back at the registration that caused it.
///
/// This is checked at host start rather than in <see cref="MartenIntegration"/> because Marten
/// applies <c>AddAsyncDaemon</c>'s mode through <c>ConfigureMarten</c>, which runs in registration
/// order — so a daemon choice made AFTER <c>IntegrateWithWolverine</c> is not yet visible while the
/// integration is configuring. By start-up the store's options are final, whatever the call order.
/// </summary>
internal class ManagedDistributionDaemonModeValidator : IHostedService
{
    private readonly DocumentStore _store;

    public ManagedDistributionDaemonModeValidator(IDocumentStore store)
    {
        _store = (DocumentStore)store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var mode = _store.Options.Projections.AsyncMode;

        if (mode is DaemonMode.Solo or DaemonMode.HotCold)
        {
            throw new InvalidOperationException(
                $"Marten's async daemon is set to {mode}, but this store also uses Wolverine-managed event subscription distribution (UseWolverineManagedEventSubscriptionDistribution = true). Wolverine replaces Marten's daemon coordination outright, so the two would run competing coordinators against the same daemon — the projections stall rather than fail, which is very hard to diagnose. Remove the Marten-side daemon choice (MartenDaemonModeIsSolo() / AddAsyncDaemon(DaemonMode.{mode})) and let Wolverine's distribution run the daemon, or turn UseWolverineManagedEventSubscriptionDistribution off.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
