using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Saga;

/// <summary>
/// Integration tests for the Marten implementation of
/// <see cref="ISagaStoreDiagnostics"/>. Stands up a real Marten host so
/// the reflection-based <c>LoadAsync</c> dispatch and
/// <c>Query&lt;TSaga&gt;()</c> path are exercised against a live
/// document store — those code paths can't be exercised by the
/// in-memory aggregator tests in CoreTests because they need a real
/// <c>IDocumentStore</c>.
/// </summary>
public class marten_saga_store_diagnostics_tests : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<DiagSaga>();

                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "saga_diag";
                    x.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

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
    public async Task registered_saga_types_includes_marten_owned_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagaTypesAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.SagaType.FullName == typeof(DiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Marten");
        diag.StartingMessages.Select(m => m.FullName).ShouldContain(typeof(StartDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagSaga).FullName!, Guid.NewGuid(), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        // Three sagas in flight — the diagnostic peek should surface
        // all three (or at least the requested top-N).
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "two"));
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "three"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(typeof(DiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(DiagSaga).FullName);
    }

    [Fact]
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var read = await diagnostics.ReadSagaAsync("Some.Unknown.Saga", Guid.NewGuid(), CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}

public record StartDiagSaga(Guid Id, string Note);

public class DiagSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
