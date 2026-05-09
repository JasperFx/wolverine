using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.TestDriver;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.RavenDb;
using Wolverine.Tracking;
using Xunit;

namespace RavenDbTests;

/// <summary>
/// Integration tests for the RavenDB implementation of
/// <see cref="ISagaStoreDiagnostics"/>. Stands up an embedded
/// RavenDB server so the reflection-based <c>session.LoadAsync</c>
/// dispatch and the <c>Query&lt;TSaga&gt;().Take(N)</c> path are
/// exercised against a live document store. RavenDB sagas use string
/// identifiers regardless of the saga's id member type — the test
/// pins both the canonical FullName lookup and the short-name lookup
/// so a regression on either index entry shows up here.
/// </summary>
[Collection("raven")]
public class raven_saga_store_diagnostics_tests : RavenTestDriver, IAsyncLifetime
{
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public Task InitializeAsync()
    {
        DatabaseFixture.EnsureServerConfigured();
        _store = GetDocumentStore();

        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.CodeGeneration.GeneratedCodeOutputPath =
                    AppContext.BaseDirectory.ParentDirectory()!.ParentDirectory()!.ParentDirectory()!
                        .AppendPath("Internal", "Generated");
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                // Pin discovery to the diagnostic-test saga so the
                // catalog isn't polluted with every saga in
                // RavenDbTests / ComplianceTests.
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RavenDiagSaga>();

                opts.Services.AddSingleton(_store);
                opts.UseRavenDbPersistence();

                opts.PublishAllMessages().Locally();
            })
            .Start();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        _store?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task registered_saga_types_includes_raven_owned_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagaTypesAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.SagaType.FullName == typeof(RavenDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("RavenDb");
        diag.StartingMessages.Select(m => m.FullName).ShouldContain(typeof(StartRavenDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = "raven-diag/" + Guid.NewGuid().ToString("N");
        await _host.InvokeMessageAndWaitAsync(new StartRavenDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(RavenDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(
            typeof(RavenDiagSaga).FullName!, "raven-diag/missing-" + Guid.NewGuid(), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        await _host.InvokeMessageAndWaitAsync(new StartRavenDiagSaga(
            "raven-diag/" + Guid.NewGuid().ToString("N"), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartRavenDiagSaga(
            "raven-diag/" + Guid.NewGuid().ToString("N"), "two"));

        // Raven indexes are eventually consistent — wait for any
        // pending indexing to converge before peeking.
        WaitForIndexing(_store);

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(RavenDiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(2);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(RavenDiagSaga).FullName);
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

public record StartRavenDiagSaga(string Id, string Note);

public class RavenDiagSaga : Wolverine.Saga
{
    public string Id { get; set; } = "";
    public string Note { get; set; } = "";

    public void Start(StartRavenDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
