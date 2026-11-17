using JasperFx.Events;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.EventSourcing.Distribution;

/// <summary>
///     GH-5141: the projection coordinator of an event store whose projections and subscriptions run as
///     Wolverine agents (<c>UseWolverineManagedEventSubscriptionDistribution</c>), written once against the
///     JasperFx <see cref="IProjectionCoordinator" /> for Marten, Polecat and Fisher alike. Each integration
///     derives a thin type from it that adds its own coordinator interface, so code resolving the store's own
///     spelling of <c>IProjectionCoordinator</c> keeps finding it.
/// </summary>
internal class WolverineProjectionCoordinator : IProjectionCoordinator
{
    private readonly EventStoreAgents _storeAgents;

    public WolverineProjectionCoordinator(EventSubscriptionAgentFamily agents, IEventStore store)
    {
        _storeAgents = agents.FindStore(store.Identity);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _storeAgents.StartAllAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return _storeAgents.StopAllAsync(cancellationToken);
    }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        return _storeAgents.DaemonForMainDatabase();
    }

    public ValueTask<IProjectionDaemon> DaemonForMainDatabaseAsync()
    {
        return _storeAgents.DaemonForMainDatabaseAsync();
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        return _storeAgents.DaemonForDatabase(databaseIdentifier);
    }

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        return _storeAgents.AllDaemonsAsync();
    }

    public Task PauseAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    public Task ResumeAsync()
    {
        return StartAsync(CancellationToken.None);
    }
}

/// <summary>
///     GH-5141: refuses a host that combines Wolverine-managed event subscription distribution with an event
///     store's own daemon hosting (Marten's <c>AddAsyncDaemon(Solo|HotCold)</c>, Polecat's
///     <c>AddAsyncDaemon(Solo|HotCold)</c>, Fisher's <c>AddAsyncDaemon(DaemonMode.Solo)</c>). One validator for
///     all three integrations; each supplies the check for its store and the wording of the remedy.
/// </summary>
/// <remarks>
///     <para>
///         That combination is never valid. The store's own daemon starts the shards while Wolverine's
///         distribution drives the same projections as agents. The two compete, and the symptom is not an error
///         but a HANG: a tracked catch-up that never completes (GH-3388). It cost a user a day to find, and
///         nothing in the code that stalls points back at the registration that caused it.
///     </para>
///     <para>
///         It is checked at host start rather than when the integration is configured because the store applies
///         <c>AddAsyncDaemon</c>'s mode through its own configuration callbacks, which run in registration order, so
///         a daemon choice made after <c>IntegrateWithWolverine</c> is not yet visible while the integration is
///         configuring. By start-up the store's options are final, whatever the call order.
///     </para>
/// </remarks>
internal class ManagedDistributionDaemonModeValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly Func<IServiceProvider, string?> _findCompetingDaemon;

    /// <param name="services">The application container</param>
    /// <param name="findCompetingDaemon">
    ///     Returns the message to refuse the host with when the store hosts its own daemon, or null when it does not
    /// </param>
    public ManagedDistributionDaemonModeValidator(IServiceProvider services,
        Func<IServiceProvider, string?> findCompetingDaemon)
    {
        _services = services;
        _findCompetingDaemon = findCompetingDaemon;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var message = _findCompetingDaemon(_services);
        if (message != null)
        {
            throw new InvalidOperationException(message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    ///     The refusal for a store whose own daemon is set to <paramref name="mode" />.
    /// </summary>
    /// <param name="storeDescription">"Marten's async daemon", "Polecat's async daemon for the ancillary store IMyStore", ...</param>
    /// <param name="mode">The daemon mode the store was given</param>
    /// <param name="remedy">How to take the store's own daemon choice out, in the store's vocabulary</param>
    internal static string MessageFor(string storeDescription, DaemonMode mode, string remedy) =>
        $"{storeDescription} is set to {mode}, but this store also uses Wolverine-managed event subscription distribution (UseWolverineManagedEventSubscriptionDistribution = true). Wolverine replaces the store's own daemon coordination outright, so the two would run competing coordinators against the same daemon — the projections stall rather than fail, which is very hard to diagnose. {remedy} and let Wolverine's distribution run the daemon, or turn UseWolverineManagedEventSubscriptionDistribution off.";

    /// <summary>
    ///     Register a validator for one store.
    /// </summary>
    internal static void Register(IServiceCollection services, Func<IServiceProvider, string?> findCompetingDaemon)
    {
        services.AddSingleton<IHostedService>(s => new ManagedDistributionDaemonModeValidator(s, findCompetingDaemon));
    }
}
