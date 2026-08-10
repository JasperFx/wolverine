using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
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

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PgDiagSaga>()
                    .IncludeType<UnprovisionedPgDiagSaga>();

                // Registering the saga type is what puts its table into the
                // message store's schema build, so the table exists as soon
                // as the host starts. Without this, pgdiagsaga_saga only came
                // into being when a sibling test persisted an instance first -
                // an inter-test order dependency that turned
                // read_saga_returns_null_for_missing_instance red on fresh
                // infrastructure (GH-3887).
                opts.AddSagaType<PgDiagSaga>();

                // UnprovisionedPgDiagSaga is deliberately NOT registered via
                // AddSagaType and never started: it pins the diagnostics
                // API's graceful handling of a declared-but-never-provisioned
                // saga (no table yet) below.

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_diag_pg");
                opts.PublishAllMessages().Locally();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
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

    [Fact]
    public async Task declared_but_never_provisioned_saga_reads_as_empty_instead_of_42P01()
    {
        // AddSagaType is optional, so a correctly configured application can
        // have a known saga type whose table does not exist until the first
        // instance is persisted. The diagnostics surface must treat that
        // state as "no instances", not surface a raw undefined-table error
        // (GH-3887). UnprovisionedPgDiagSaga is in the handler graph but is
        // never registered via AddSagaType and never started - and to keep
        // this deterministic on any infrastructure state, drop its table if
        // some earlier run left one behind.
        var tableName = new SagaTableDefinition(typeof(UnprovisionedPgDiagSaga), null).TableName;
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"drop table if exists saga_diag_pg.{tableName}";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var diagnostics = _host.GetRuntime().SagaStorage;

        var read = await diagnostics.ReadSagaAsync(
            typeof(UnprovisionedPgDiagSaga).FullName!, Guid.NewGuid().ToString("N"), CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync(
            typeof(UnprovisionedPgDiagSaga).FullName!, 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}

public record StartPgDiagSaga(string Id, string Note);

public record StartUnprovisionedPgDiagSaga(string Id);

/// <summary>
/// Deliberately never registered with <c>AddSagaType</c> and never started
/// by any test: its table must not exist, pinning the graceful "declared
/// but never provisioned" path in the saga diagnostics (GH-3887).
/// </summary>
public class UnprovisionedPgDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }

    public static UnprovisionedPgDiagSaga Start(StartUnprovisionedPgDiagSaga cmd)
    {
        return new UnprovisionedPgDiagSaga { Id = cmd.Id };
    }
}

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
