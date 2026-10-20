using JasperFx.Events.Daemon;

namespace Wolverine.Polecat.Distribution;

/// <summary>
///     Coordinates the lifecycle of projection daemons across databases.
///     Port of Marten's IProjectionCoordinator for use with Polecat.
/// </summary>
/// <remarks>
///     GH-5092: extends the JasperFx <see cref="JasperFx.Events.Daemon.IProjectionCoordinator" /> so a
///     store-agnostic caller (CritterWatch, Marten-style diagnostics) finds a Polecat coordinator through
///     the same interface as a Marten one. The members are redeclared so code compiled against the
///     earlier shape of this interface keeps binding to them.
/// </remarks>
public interface IProjectionCoordinator : JasperFx.Events.Daemon.IProjectionCoordinator
{
    ValueTask<IProjectionDaemon> DaemonForMainDatabaseAsync();
    new ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier);
    new ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync();
    new Task PauseAsync();
    new Task ResumeAsync();
}

public interface IProjectionCoordinator<T> : IProjectionCoordinator where T : global::Polecat.IDocumentStore
{
}
