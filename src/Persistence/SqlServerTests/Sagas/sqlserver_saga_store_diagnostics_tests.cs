using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
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

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MsSqlDiagSaga>()
                    .IncludeType<UnprovisionedMsSqlDiagSaga>();

                // Same fix as the Postgres twin (GH-3887): registering the
                // saga type puts its table into the message store's schema
                // build so it exists deterministically at host start, rather
                // than only after a sibling test persists an instance.
                opts.AddSagaType<MsSqlDiagSaga>();

                // UnprovisionedMsSqlDiagSaga is deliberately NOT registered
                // via AddSagaType and never started - see the
                // declared-but-never-provisioned test below.

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_diag_mssql");
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

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(MsSqlDiagSaga).FullName);
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

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        // Parity with the Postgres twin: reads a known saga type without
        // persisting an instance first. Only safe on fresh infrastructure
        // because the fixture now calls AddSagaType (GH-3887).
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(
            typeof(MsSqlDiagSaga).FullName!, Guid.NewGuid().ToString("N"), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task declared_but_never_provisioned_saga_reads_as_empty_instead_of_invalid_object_name()
    {
        // The SQL Server flavor of the GH-3887 product half: a known saga
        // type with no table yet (AddSagaType omitted, nothing persisted)
        // must read as "no instances" rather than surfacing error 208
        // "Invalid object name". Drop the table first so the precondition
        // holds on any infrastructure state.
        var tableName = new SagaTableDefinition(typeof(UnprovisionedMsSqlDiagSaga), null).TableName;
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"drop table if exists saga_diag_mssql.{tableName}";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var diagnostics = _host.GetRuntime().SagaStorage;

        var read = await diagnostics.ReadSagaAsync(
            typeof(UnprovisionedMsSqlDiagSaga).FullName!, Guid.NewGuid().ToString("N"), CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync(
            typeof(UnprovisionedMsSqlDiagSaga).FullName!, 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}

public record StartMsSqlDiagSaga(string Id, string Note);

public record StartUnprovisionedMsSqlDiagSaga(string Id);

/// <summary>
/// Deliberately never registered with <c>AddSagaType</c> and never started
/// by any test: its table must not exist, pinning the graceful "declared
/// but never provisioned" path in the saga diagnostics (GH-3887).
/// </summary>
public class UnprovisionedMsSqlDiagSaga : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }

    public static UnprovisionedMsSqlDiagSaga Start(StartUnprovisionedMsSqlDiagSaga cmd)
    {
        return new UnprovisionedMsSqlDiagSaga { Id = cmd.Id };
    }
}

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
