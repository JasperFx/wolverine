using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace SqlServerTests.Sagas;

/// <summary>
/// Integration tests for the lightweight RDBMS implementation of
/// <see cref="ISagaStoreDiagnostics"/> backed by SQL Server. The
/// dialect-detection branch in
/// <c>DatabaseSagaStoreDiagnostics.renderTopNQuery</c> emits
/// <c>SELECT TOP N</c> for <c>SqlClient</c> connections — distinct
/// from the <c>LIMIT</c> form Postgres / MySQL / SQLite use — so it
/// gets its own integration test rather than relying on the Postgres
/// fixture to cover both shapes.
/// </summary>
public class sqlserver_saga_store_diagnostics_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MsSqlDiagSaga>();
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_diag_mssql");
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
        var registered = await diagnostics.GetRegisteredSagaTypesAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.SagaType.FullName == typeof(MsSqlDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Database");
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid().ToString("N");
        await _host.InvokeMessageAndWaitAsync(new StartMsSqlDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(MsSqlDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task list_saga_instances_uses_top_n_clause_for_sqlserver()
    {
        // Two starts is enough — what we really care about is that
        // the top-N query renders as SELECT TOP for SqlClient and
        // returns rows. If renderTopNQuery picked the wrong dialect,
        // SQL Server would throw a syntax error on LIMIT.
        await _host.InvokeMessageAndWaitAsync(new StartMsSqlDiagSaga(Guid.NewGuid().ToString("N"), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartMsSqlDiagSaga(Guid.NewGuid().ToString("N"), "two"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(MsSqlDiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(2);
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

public record StartMsSqlDiagSaga(string Id, string Note);

public class MsSqlDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }
    public string Note { get; set; } = "";

    public static MsSqlDiagSaga Start(StartMsSqlDiagSaga cmd, ILogger<MsSqlDiagSaga> logger)
    {
        logger.LogInformation("Starting MsSqlDiagSaga {Id}", cmd.Id);
        return new MsSqlDiagSaga { Id = cmd.Id, Note = cmd.Note };
    }
}
