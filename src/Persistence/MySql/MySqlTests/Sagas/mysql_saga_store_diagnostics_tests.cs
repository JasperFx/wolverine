using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace MySqlTests.Sagas;

/// <summary>
/// Integration tests for the lightweight RDBMS implementation of
/// <see cref="ISagaStoreDiagnostics"/> backed by MySQL. Shares the
/// <c>LIMIT</c> top-N path with the Postgres / SQLite fixtures —
/// having a dedicated MySQL host here pins the
/// <c>MySqlConnector</c>-namespaced connection's dialect detection so
/// a future tweak to <c>renderTopNQuery</c> can't silently break
/// MySQL while passing for Postgres.
/// </summary>
public class mysql_saga_store_diagnostics_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<MySqlDiagSaga>();
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "saga_diag_mysql");
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

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(MySqlDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Database");
    }

    [Fact]
    public async Task read_and_list_round_trip_against_mysql()
    {
        var sagaId = Guid.NewGuid().ToString("N");
        await _host.InvokeMessageAndWaitAsync(new StartMySqlDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;

        var read = await diagnostics.ReadSagaAsync(typeof(MySqlDiagSaga).FullName!, sagaId, CancellationToken.None);
        read.ShouldNotBeNull();
        read.State.GetProperty("Note").GetString().ShouldBe("alpha");

        var list = await diagnostics.ListSagaInstancesAsync(typeof(MySqlDiagSaga).FullName!, 10, CancellationToken.None);
        list.ShouldNotBeEmpty();
    }
}

public record StartMySqlDiagSaga(string Id, string Note);

public class MySqlDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }
    public string Note { get; set; } = "";

    public static MySqlDiagSaga Start(StartMySqlDiagSaga cmd, ILogger<MySqlDiagSaga> logger)
    {
        logger.LogInformation("Starting MySqlDiagSaga {Id}", cmd.Id);
        return new MySqlDiagSaga { Id = cmd.Id, Note = cmd.Note };
    }
}
