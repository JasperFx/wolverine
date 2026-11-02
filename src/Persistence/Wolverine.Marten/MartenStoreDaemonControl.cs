using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Marten;

/// <summary>
/// GH-5317: one Marten store's projection coordinator and document store, resolved without the caller
/// knowing the store's marker type. Each ancillary store registers one of these from
/// <c>IntegrateWithWolverine()</c>; the main store is found directly in <see cref="All"/>.
/// </summary>
internal abstract class MartenStoreDaemonControl
{
    /// <summary>
    /// Used in error messages, e.g. "the main Marten store"
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Suffix for the tracked-session envelope message types, matching the single-store helpers
    /// </summary>
    public abstract string PauseMessageType { get; }

    public abstract string CatchUpMessageType { get; }

    public abstract global::Marten.Events.Daemon.Coordination.IProjectionCoordinator? FindCoordinator(
        IServiceProvider services);

    public abstract IDocumentStore FindStore(IServiceProvider services);

    /// <summary>
    /// The main store (when it has a projection coordinator) followed by every ancillary store integrated
    /// with Wolverine, in registration order
    /// </summary>
    public static IReadOnlyList<MartenStoreDaemonControl> All(IServiceProvider services)
    {
        var list = new List<MartenStoreDaemonControl>();

        // A host with only ancillary stores, or a main store with no async daemon and no Wolverine-managed
        // distribution, has no main coordinator, and so nothing to catch up for the main store
        if (services.GetService<global::Marten.Events.Daemon.Coordination.IProjectionCoordinator>() != null)
        {
            list.Add(new MainMartenStoreDaemonControl());
        }

        list.AddRange(services.GetServices<MartenStoreDaemonControl>());

        return list;
    }
}

internal class MainMartenStoreDaemonControl : MartenStoreDaemonControl
{
    public override string Description => "the main Marten store";
    public override string PauseMessageType => "Pause:Marten:Daemons";
    public override string CatchUpMessageType => "CatchUp:Marten:DaemonActivity";

    public override global::Marten.Events.Daemon.Coordination.IProjectionCoordinator? FindCoordinator(
        IServiceProvider services)
    {
        return services.GetService<global::Marten.Events.Daemon.Coordination.IProjectionCoordinator>();
    }

    public override IDocumentStore FindStore(IServiceProvider services)
    {
        return services.GetRequiredService<IDocumentStore>();
    }
}

internal class AncillaryMartenStoreDaemonControl<T> : MartenStoreDaemonControl where T : class, IDocumentStore
{
    public override string Description => $"the ancillary Marten store {typeof(T).NameInCode()}";
    public override string PauseMessageType => "Pause:Marten:Daemons:" + typeof(T).NameInCode();
    public override string CatchUpMessageType => "CatchUp:Marten:DaemonActivity:" + typeof(T).FullNameInCode();

    public override global::Marten.Events.Daemon.Coordination.IProjectionCoordinator? FindCoordinator(
        IServiceProvider services)
    {
        return services.GetService<global::Marten.Events.Daemon.Coordination.IProjectionCoordinator<T>>();
    }

    public override IDocumentStore FindStore(IServiceProvider services)
    {
        return services.GetRequiredService<T>();
    }
}
