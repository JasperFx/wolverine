using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Sqlite;
using Wolverine.Tracking;
using Xunit;

namespace SqliteTests.Sagas;

/// <summary>
/// Integration tests for the lightweight RDBMS implementation of
/// <see cref="ISagaStoreDiagnostics"/> backed by SQLite. Exercises
/// the same SQL-based read/list path as the Postgres / SQL Server
/// fixtures but in-process — no docker container required, so this
/// fixture runs every CI machine and acts as the always-available
/// smoke test for the lightweight saga diagnostic surface.
/// </summary>
public class sqlite_saga_store_diagnostics_tests : SqliteContext, IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(sqlite_saga_store_diagnostics_tests));

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<SqliteDiagSaga>();
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task registered_saga_types_includes_database_owned_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagaTypesAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.SagaType.FullName == typeof(SqliteDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Database");
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid().ToString("N");
        await _host.InvokeMessageAndWaitAsync(new StartSqliteDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(SqliteDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        await _host.InvokeMessageAndWaitAsync(new StartSqliteDiagSaga(Guid.NewGuid().ToString("N"), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartSqliteDiagSaga(Guid.NewGuid().ToString("N"), "two"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(SqliteDiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(2);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(SqliteDiagSaga).FullName);
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

public record StartSqliteDiagSaga(string Id, string Note);

public class SqliteDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }
    public string Note { get; set; } = "";

    public static SqliteDiagSaga Start(StartSqliteDiagSaga cmd, ILogger<SqliteDiagSaga> logger)
    {
        logger.LogInformation("Starting SqliteDiagSaga {Id}", cmd.Id);
        return new SqliteDiagSaga { Id = cmd.Id, Note = cmd.Note };
    }
}
