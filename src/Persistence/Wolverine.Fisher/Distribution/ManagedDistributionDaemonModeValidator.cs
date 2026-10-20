using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Fisher.Distribution;

/// <summary>
///     GH-5094, the Fisher twin of Wolverine.Marten's GH-3388 check: refuses a host that combines
///     Wolverine-managed event subscription distribution with Fisher's own <c>AddAsyncDaemon(DaemonMode.Solo)</c>
///     on the main store. Fisher's Solo mode hosts its own daemon as JasperFx's
///     <see cref="JasperFx.Events.Daemon.IProjectionCoordinator" />, and Wolverine's agents would run the same
///     projections against the same SQLite file beside it. Checked at host start because <c>AddAsyncDaemon</c>
///     may be called before or after <c>IntegrateWithWolverine</c>.
/// </summary>
internal class ManagedDistributionDaemonModeValidator : IHostedService
{
    private readonly IServiceProvider _services;

    public ManagedDistributionDaemonModeValidator(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_services.GetServices<JasperFx.Events.Daemon.IProjectionCoordinator>().Any())
        {
            throw new InvalidOperationException(Message("the Fisher store"));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static string Message(string store) =>
        $"Fisher's async daemon for {store} is hosted by AddAsyncDaemon(DaemonMode.Solo), but this host also uses Wolverine-managed event subscription distribution (UseWolverineManagedEventSubscriptionDistribution = true). Wolverine runs the projections and subscriptions as agents, so the two would project the same events into the same SQLite file. Remove AddAsyncDaemon() (or use DaemonMode.ExternallyManaged) and let Wolverine's distribution run the daemon, or turn UseWolverineManagedEventSubscriptionDistribution off.";
}

/// <summary>
///     GH-5094: the same check for an ancillary Fisher store, whose Solo daemon is registered as JasperFx's
///     <see cref="JasperFx.Events.Daemon.IProjectionCoordinator{T}" />. The flag lives on the main integration,
///     so it is read at host start too.
/// </summary>
internal class ManagedDistributionDaemonModeValidator<T> : IHostedService where T : class
{
    private readonly IServiceProvider _services;

    public ManagedDistributionDaemonModeValidator(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService<FisherIntegration>() is { UseWolverineManagedEventSubscriptionDistribution: true }
            && _services.GetServices<JasperFx.Events.Daemon.IProjectionCoordinator<T>>().Any())
        {
            throw new InvalidOperationException(
                ManagedDistributionDaemonModeValidator.Message($"the ancillary store {typeof(T).Name}"));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
