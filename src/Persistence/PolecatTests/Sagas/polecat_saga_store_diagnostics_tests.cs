using IntegrationTests;
using JasperFx.Documents;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

/// <summary>
///     GH-5142. The saga explorer over a Polecat host. Polecat used to register the lightweight-RDBMS
///     <c>DatabaseSagaStoreDiagnostics</c>, which reads Wolverine's own SQL Server saga tables, but a Polecat saga
///     is a Polecat document, so those tables are never written and the explorer saw nothing. Polecat now
///     shares Marten's document-store diagnostics; this is the Polecat twin of
///     <c>marten_saga_store_diagnostics_tests</c>.
/// </summary>
public class polecat_saga_store_diagnostics_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PcDiagSaga>()
                    .IncludeType<PcIntDiagSaga>();

                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "saga_diag";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await ((DocumentStore)_host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private ISagaStoreDiagnostics diagnostics => _host.GetRuntime().SagaStorage;

    [Fact]
    public async Task registered_saga_types_include_the_polecat_saga()
    {
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(PcDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Polecat");
        diag.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(StartPcDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_the_state_of_an_existing_instance()
    {
        var sagaId = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(sagaId, "alpha"));

        var state = await diagnostics.ReadSagaAsync(typeof(PcDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_a_missing_instance()
    {
        var state = await diagnostics.ReadSagaAsync(typeof(PcDiagSaga).FullName!, Guid.NewGuid(),
            CancellationToken.None);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task read_saga_with_an_int_identity()
    {
        var sagaId = Random.Shared.Next(1, int.MaxValue);
        await _host.InvokeMessageAndWaitAsync(new StartPcIntDiagSaga(sagaId, "int keyed"));

        var state = await diagnostics.ReadSagaAsync(typeof(PcIntDiagSaga).FullName!, sagaId.ToString(),
            CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("int keyed");
    }

    [Fact]
    public async Task list_saga_instances_returns_the_sagas_in_flight()
    {
        await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(Guid.NewGuid(), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(Guid.NewGuid(), "two"));
        await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(Guid.NewGuid(), "three"));

        var instances = await diagnostics.ListSagaInstancesAsync(typeof(PcDiagSaga).FullName!, 10,
            CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(PcDiagSaga).FullName);
    }

    [Fact]
    public async Task query_sagas_filters_orders_pages_and_counts()
    {
        var tag = Guid.NewGuid().ToString("N");
        foreach (var n in new[] { "b", "c", "a", "d" })
        {
            await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(Guid.NewGuid(), $"{tag}-{n}"));
        }

        await _host.InvokeMessageAndWaitAsync(new StartPcDiagSaga(Guid.NewGuid(), "someone-else"));

        var result = await diagnostics.QuerySagasAsync(typeof(PcDiagSaga).FullName!,
            new DocumentQueryOptions(2, 3)
            {
                Where = "Note.StartsWith(@0)",
                Arguments = [tag],
                OrderBy = "Note desc"
            }, CancellationToken.None);

        result.TotalCount.ShouldBe(4);
        result.Instances.Select(i => i.State.GetProperty("Note").GetString()).ShouldBe([$"{tag}-a"]);
    }
}

public record StartPcDiagSaga(Guid Id, string Note);

[WolverineIgnore]
public class PcDiagSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartPcDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}

public record StartPcIntDiagSaga(int Id, string Note);

[WolverineIgnore]
public class PcIntDiagSaga : Wolverine.Saga
{
    public int Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartPcIntDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
