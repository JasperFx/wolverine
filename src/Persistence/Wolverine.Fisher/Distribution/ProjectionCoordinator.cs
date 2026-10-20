using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;
using Fisher;

namespace Wolverine.Fisher.Distribution;

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
        _logger.LogInformation("Starting Fisher projection coordinator");
        var daemon = await DaemonForMainDatabaseAsync();
        await daemon.StartAllAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Fisher projection coordinator");
        var daemon = await DaemonForMainDatabaseAsync();
        await daemon.StopAllAsync().ConfigureAwait(false);
    }

    private IProjectionDaemon? _daemon;

    public async ValueTask<IProjectionDaemon> DaemonForMainDatabaseAsync()
    {
        if (_daemon != null) return _daemon;

        var documentStore = _store.As<DocumentStore>();

        _daemon = await documentStore.BuildProjectionDaemonAsync(logger: _logger);
        return _daemon;
    }

    // GH-5092: the JasperFx IProjectionCoordinator member, now that this interface extends it. The daemon
    // is built asynchronously, by StartAsync in the normal course, so the synchronous member can only hand
    // back one that already exists.
    IProjectionDaemon JasperFx.Events.Daemon.IProjectionCoordinator.DaemonForMainDatabase()
    {
        return _daemon ?? throw new InvalidOperationException(
            "The Fisher projection daemon has not been built yet because the projection coordinator has not started. Use DaemonForMainDatabaseAsync() instead.");
    }

    public async ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        var documentStore = _store.As<DocumentStore>();
        return await documentStore.BuildProjectionDaemonAsync(databaseIdentifier, _logger).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        // For single-database, return just the main daemon
        var daemon = await DaemonForMainDatabaseAsync();
        return [daemon];
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
