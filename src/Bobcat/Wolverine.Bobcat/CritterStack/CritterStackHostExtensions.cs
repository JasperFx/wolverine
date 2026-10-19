using JasperFx.CommandLine.Descriptions;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack;

/// <summary>
/// Host-level Critter Stack helpers, hanging off the <see cref="IHost"/> /
/// <see cref="IServiceProvider"/> so a fixture's setup, a host resource's reset hook or a test's
/// arrange can use them without any scenario scope.
/// </summary>
/// <remarks>
/// Marten (<c>AddMarten</c>), Polecat (<c>AddPolecat</c>) and Fisher (<c>AddFisher</c>) all
/// register their store as <see cref="IEventStore"/>, so <see cref="EventStores(IServiceProvider)"/>
/// is how a spec project finds the store without referencing the store's package.
/// </remarks>
public static class CritterStackHostExtensions
{
    /// <summary>Every event store registered in the container, in registration order.</summary>
    public static IReadOnlyList<IEventStore> EventStores(this IServiceProvider services)
        => services.GetServices<IEventStore>().ToList();

    /// <summary>
    /// The host's default event store: the one registered in the container; or, when there are several, the
    /// one whose <see cref="IEventStore.Identity"/> name matches <paramref name="storeName"/>; or, with no
    /// <paramref name="storeName"/>, the one <b>main</b> store among them (<see cref="MainEventStores"/>), so an
    /// application with one main store and any number of ancillary ones (Marten's <c>AddMartenStore&lt;T&gt;</c>,
    /// Fisher's <c>AddFisherStore&lt;T&gt;</c>, an embedded CritterWatch console's own store) needs no name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No store is registered; or several are, <paramref name="storeName"/> is null and there is not exactly one
    /// main store among them; or no store has that name. The message lists the stores and their roles.
    /// </exception>
    public static IEventStore EventStore(this IServiceProvider services, string? storeName = null)
    {
        var stores = services.EventStores();

        if (stores.Count == 0)
        {
            throw new InvalidOperationException(
                "No JasperFx.Events.IEventStore is registered in the host's container. Marten (AddMarten), Polecat " +
                "(AddPolecat) and Fisher (AddFisher) all register one — is the store wired into this host?");
        }

        if (storeName == null)
        {
            return DefaultEventStore(services, stores) ?? throw new InvalidOperationException(noDefault(services, stores));
        }

        return stores.FirstOrDefault(s => matches(s, storeName))
               ?? throw new InvalidOperationException(
                   $"No event store named '{storeName}' is registered. Known: " +
                   string.Join(", ", stores.Select(Bobcat.CritterStack.EventStores.Describe)) + ".");
    }

    /// <summary>
    /// The event stores registered as an application's <b>main</b> store — <c>AddMarten</c>, <c>AddPolecat</c>,
    /// <c>AddFisher</c> — rather than as an ancillary store under a marker interface (<c>AddMartenStore&lt;T&gt;</c>,
    /// <c>AddPolecatStore&lt;T&gt;</c>, <c>AddFisherStore&lt;T&gt;</c>), in registration order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the container, store-agnostically: a main store is the one its own product's
    /// <c>IDocumentStore</c> service resolves to. All three stores register their main store under their
    /// <c>IDocumentStore</c> interface and an ancillary store under its marker interface only, so for an
    /// ancillary store that service resolves to another instance (the main store) or to nothing. The interface
    /// is found by name on the store's own type, so Bobcat references none of the stores.
    /// </para>
    /// <para>
    /// Not the <see cref="IEventStore.Identity"/> name: <c>"main"</c> is only the default <c>StoreName</c>, which
    /// an application may change on its main store or give to an ancillary one, while the registration is what
    /// makes a store the one <c>IDocumentStore</c> means. A store with no <c>IDocumentStore</c> interface at all
    /// (a hand-written or in-memory store) is never a main store, so a host made only of those keeps asking for a
    /// <c>storeName</c>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IEventStore> MainEventStores(this IServiceProvider services)
        => services.EventStores().Where(store => IsMainEventStore(services, store)).ToList();

    /// <summary>
    /// Whether <paramref name="store"/> is registered as the application's main store, rather than as an ancillary
    /// one. See <see cref="MainEventStores"/>.
    /// </summary>
    public static bool IsMainEventStore(this IServiceProvider services, IEventStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        foreach (var service in store.GetType().GetInterfaces())
        {
            if (service.IsGenericType || service.Name != MainStoreService) continue;

            object? registered;
            try
            {
                registered = services.GetService(service);
            }
            catch (Exception)
            {
                continue;
            }

            if (ReferenceEquals(registered, store)) return true;
        }

        return false;
    }

    // The service every Critter Stack store registers its main store under: Marten.IDocumentStore,
    // Polecat.IDocumentStore, Fisher.IDocumentStore
    private const string MainStoreService = "IDocumentStore";

    /// <summary>
    /// The default store with no name to go by: the only store, else the only main store, else null.
    /// </summary>
    internal static IEventStore? DefaultEventStore(IServiceProvider services, IReadOnlyList<IEventStore> stores)
    {
        if (stores.Count == 1) return stores[0];

        var mains = stores.Where(store => IsMainEventStore(services, store)).ToList();
        return mains.Count == 1 ? mains[0] : null;
    }

    private static string noDefault(IServiceProvider services, IReadOnlyList<IEventStore> stores)
    {
        var mains = stores.Where(store => IsMainEventStore(services, store)).ToList();
        var described = string.Join(", ", stores.Select(store =>
            Bobcat.CritterStack.EventStores.Describe(store) + (mains.Contains(store) ? " [main]" : " [ancillary]")));

        var why = mains.Count == 0
            ? "none of them is registered as the application's main store (AddMarten, AddPolecat, AddFisher)"
            : $"{mains.Count} of them are registered as a main store";

        return $"The host registers {stores.Count} event stores ({described}) and {why}, so there is no default store. "
               + "Pass storeName (the store's IEventStore.Identity name) to the scenario or spec to choose the default, "
               + "or address a store per step with In<TStore>() for a store registered under a marker interface or "
               + "In(\"name\") by its name.";
    }

    /// <summary>The coordinator driving the host's projection daemon, when the host runs one.</summary>
    public static IProjectionCoordinator? ProjectionCoordinator(this IServiceProvider services)
        => services.GetService<IProjectionCoordinator>();

    /// <summary>
    /// Every projection coordinator the host runs: the main store's, registered as
    /// <see cref="IProjectionCoordinator"/>, and an ancillary store's, which Marten and Fisher register
    /// only under their own <c>IProjectionCoordinator&lt;T&gt;</c> and as an <see cref="IHostedService"/>.
    /// Each appears once, however many registrations reach it.
    /// </summary>
    public static IReadOnlyList<IProjectionCoordinator> ProjectionCoordinators(this IServiceProvider services)
    {
        var coordinators = new List<IProjectionCoordinator>();
        var seen = new HashSet<IProjectionCoordinator>(ReferenceEqualityComparer.Instance);
        var candidates = services.GetServices<IProjectionCoordinator>()
            .Concat(services.GetServices<IHostedService>().OfType<IProjectionCoordinator>());

        foreach (var coordinator in candidates)
        {
            if (seen.Add(coordinator)) coordinators.Add(coordinator);
        }

        return coordinators;
    }

    /// <summary>
    /// Delete every event and every document in every event store the host registers, keeping the
    /// schema — the between-scenario reset. Pass this as a host resource's reset hook:
    /// <c>new WebApp(reset: host =&gt; host.ResetEventStoresAsync())</c>.
    /// </summary>
    /// <remarks>
    /// bobcat#680: every projection coordinator is paused around the delete and resumed afterwards, as
    /// Marten's own <c>ResetAllMartenDataAsync</c> does. Truncating under a running async daemon left its
    /// progression ahead of the emptied store, and the daemon logged a
    /// <c>ProgressionProgressOutOfOrderException</c> per reset. The coordinators are resumed even when a
    /// reset throws, so a failed reset does not leave the host's projections stopped.
    /// </remarks>
    public static async Task ResetEventStoresAsync(this IServiceProvider services, CancellationToken token = default)
    {
        var coordinators = services.ProjectionCoordinators();
        foreach (var coordinator in coordinators)
            await coordinator.PauseAsync().ConfigureAwait(false);

        try
        {
            foreach (var store in services.EventStores())
                await Bobcat.CritterStack.EventStores.ResetAllDataAsync(store, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var coordinator in coordinators)
                await coordinator.ResumeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ResetEventStoresAsync(IServiceProvider, CancellationToken)"/>
    public static Task ResetEventStoresAsync(this IHost host, CancellationToken token = default)
        => host.Services.ResetEventStoresAsync(token);

    /// <summary>
    /// Run <see cref="IStatefulResource.ClearState"/> on every stateful resource the host's
    /// <see cref="ISystemPart"/>s describe — Wolverine's envelope storage and transports, and any store
    /// whose database supports rewindable state. This is what <c>jasperfx resources clear</c> does,
    /// minus the schema <c>Setup</c> that command also runs per resource. It does <b>not</b> replace
    /// <see cref="ResetEventStoresAsync(IServiceProvider, CancellationToken)"/>: Marten 9.22's database
    /// resource reports no rewindable state, so its <c>ClearState</c> is a no-op.
    /// </summary>
    public static async Task ClearStatefulResourcesAsync(this IServiceProvider services, CancellationToken token = default)
    {
        foreach (var part in services.GetServices<ISystemPart>())
        {
            foreach (var resource in await part.FindResources().ConfigureAwait(false))
                await resource.ClearState(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="ClearStatefulResourcesAsync(IServiceProvider, CancellationToken)"/>
    public static Task ClearStatefulResourcesAsync(this IHost host, CancellationToken token = default)
        => host.Services.ClearStatefulResourcesAsync(token);

    /// <summary>
    /// Wait for every async projection on the named (or only) store to catch up with its high-water
    /// mark. See <see cref="Bobcat.CritterStack.EventStores.WaitForNonStaleProjectionsAsync"/>.
    /// </summary>
    public static Task WaitForNonStaleProjectionsAsync(
        this IServiceProvider services,
        TimeSpan? timeout = null,
        string? storeName = null,
        CancellationToken token = default)
        => Bobcat.CritterStack.EventStores.WaitForNonStaleProjectionsAsync(
            services.EventStore(storeName), timeout, services.ProjectionCoordinator(), token);

    /// <inheritdoc cref="WaitForNonStaleProjectionsAsync(IServiceProvider, TimeSpan?, string?, CancellationToken)"/>
    public static Task WaitForNonStaleProjectionsAsync(
        this IHost host,
        TimeSpan? timeout = null,
        string? storeName = null,
        CancellationToken token = default)
        => host.Services.WaitForNonStaleProjectionsAsync(timeout, storeName, token);

    private static bool matches(IEventStore store, string storeName)
    {
        try
        {
            return string.Equals(store.Identity.Name, storeName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
