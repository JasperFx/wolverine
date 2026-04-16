using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;
using Polecat;

namespace Wolverine.Polecat.Distribution;

internal class ProjectionCoordinator<T> : ProjectionCoordinator, IProjectionCoordinator<T>
    where T : IDocumentStore
{
    public ProjectionCoordinator(T store, ILogger<IProjectionCoordinator> logger) : base(store, logger)
    {
    }
}

internal class ProjectionCoordinator : IProjectionCoordinator
{
    private readonly IDocumentStore _store;
    private readonly ILogger<IProjectionCoordinator> _logger;

    public ProjectionCoordinator(IDocumentStore store, ILogger<IProjectionCoordinator> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Polecat projection coordinator");
        var daemon = DaemonForMainDatabase();
        await daemon.StartAllAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Polecat projection coordinator");
        var daemon = DaemonForMainDatabase();
        await daemon.StopAllAsync().ConfigureAwait(false);
    }

    private IProjectionDaemon? _daemon;

    public IProjectionDaemon DaemonForMainDatabase()
    {
        if (_daemon != null) return _daemon;

        var documentStore = _store.As<DocumentStore>();
        // BuildProjectionDaemonAsync is async in Polecat, but we need sync access here.
        // Use GetAwaiter().GetResult() since this is a lazy initialization that only runs once.
        _daemon = documentStore.BuildProjectionDaemonAsync(logger: _logger).AsTask().GetAwaiter().GetResult();
        return _daemon;
    }

    public async ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        var documentStore = _store.As<DocumentStore>();
        return await documentStore.BuildProjectionDaemonAsync(databaseIdentifier, _logger).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        // For single-database, return just the main daemon
        var daemon = DaemonForMainDatabase();
        IReadOnlyList<IProjectionDaemon> list = new[] { daemon };
        return list;
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
