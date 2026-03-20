using JasperFx.Events.Daemon;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Polecat.Distribution;

/// <summary>
///     Coordinates the lifecycle of projection daemons across databases.
///     Port of Marten's IProjectionCoordinator for use with Polecat.
/// </summary>
public interface IProjectionCoordinator : IHostedService
{
    IProjectionDaemon DaemonForMainDatabase();
    ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier);
    ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync();
    Task PauseAsync();
    Task ResumeAsync();
}

public interface IProjectionCoordinator<T> : IProjectionCoordinator where T : global::Polecat.IDocumentStore
{
}
