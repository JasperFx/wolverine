using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests.Sagas;

/// <summary>
/// Integration tests for the lightweight RDBMS implementation of
/// <see cref="ISagaStoreDiagnostics"/> backed by PostgreSQL. Stands up
/// a real Wolverine host using
/// <c>PersistMessagesWithPostgresql</c> + Wolverine's per-saga-type
/// table layout, then drives the diagnostic surface end-to-end.
/// Validates the dialect-detection branch in
/// <see cref="Wolverine.RDBMS.Sagas.DatabaseSagaStoreDiagnostics"/> for
/// the <c>LIMIT</c> top-N path that Postgres / MySQL / SQLite share.
/// </summary>
public class postgres_saga_store_diagnostics_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PgDiagSaga>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_diag_pg");
                opts.PublishAllMessages().Locally();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task registered_saga_types_includes_database_owned_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(PgDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Database");
        diag.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(StartPgDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid().ToString("N");
        await _host.InvokeMessageAndWaitAsync(new StartPgDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(PgDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(
            typeof(PgDiagSaga).FullName!, Guid.NewGuid().ToString("N"), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        await _host.InvokeMessageAndWaitAsync(new StartPgDiagSaga(Guid.NewGuid().ToString("N"), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartPgDiagSaga(Guid.NewGuid().ToString("N"), "two"));
        await _host.InvokeMessageAndWaitAsync(new StartPgDiagSaga(Guid.NewGuid().ToString("N"), "three"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(PgDiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(PgDiagSaga).FullName);
    }

    [Fact]
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var read = await diagnostics.ReadSagaAsync("Some.Unknown.Saga", "anything", CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}

public record StartPgDiagSaga(string Id, string Note);

public class PgDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }
    public string Note { get; set; } = "";

    public static PgDiagSaga Start(StartPgDiagSaga cmd, ILogger<PgDiagSaga> logger)
    {
        logger.LogInformation("Starting PgDiagSaga {Id}", cmd.Id);
        return new PgDiagSaga { Id = cmd.Id, Note = cmd.Note };
    }
}
