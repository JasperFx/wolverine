using JasperFx.Documents;
using Microsoft.Extensions.Hosting;
using Fisher;
using JasperFx;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests.Sagas;

/// <summary>
///     GH-5142. The saga explorer over a Fisher host. Fisher used to register the lightweight-RDBMS
///     <c>DatabaseSagaStoreDiagnostics</c>, which reads Wolverine's own SQLite saga tables, but a Fisher saga
///     is a Fisher document, so those tables are never written and the explorer saw nothing. Fisher now
///     shares Marten's document-store diagnostics; this is the Fisher twin of
///     <c>marten_saga_store_diagnostics_tests</c>.
/// </summary>
public class fisher_saga_store_diagnostics_tests : IAsyncLifetime
{
    private FisherTestDatabase _database = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _database = Servers.CreateDatabase("saga_diag");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FiDiagSaga>()
                    .IncludeType<FiIntDiagSaga>();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(_database.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    private ISagaStoreDiagnostics diagnostics => _host.GetRuntime().SagaStorage;

    [Fact]
    public async Task registered_saga_types_include_the_fisher_saga()
    {
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(FiDiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Fisher");
        diag.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(StartFiDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_the_state_of_an_existing_instance()
    {
        var sagaId = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(sagaId, "alpha"));

        var state = await diagnostics.ReadSagaAsync(typeof(FiDiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_a_missing_instance()
    {
        var state = await diagnostics.ReadSagaAsync(typeof(FiDiagSaga).FullName!, Guid.NewGuid(),
            CancellationToken.None);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task read_saga_with_an_int_identity()
    {
        var sagaId = Random.Shared.Next(1, int.MaxValue);
        await _host.InvokeMessageAndWaitAsync(new StartFiIntDiagSaga(sagaId, "int keyed"));

        var state = await diagnostics.ReadSagaAsync(typeof(FiIntDiagSaga).FullName!, sagaId.ToString(),
            CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("int keyed");
    }

    [Fact]
    public async Task list_saga_instances_returns_the_sagas_in_flight()
    {
        await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(Guid.NewGuid(), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(Guid.NewGuid(), "two"));
        await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(Guid.NewGuid(), "three"));

        var instances = await diagnostics.ListSagaInstancesAsync(typeof(FiDiagSaga).FullName!, 10,
            CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(FiDiagSaga).FullName);
    }

    [Fact]
    public async Task query_sagas_filters_orders_pages_and_counts()
    {
        var tag = Guid.NewGuid().ToString("N");
        foreach (var n in new[] { "b", "c", "a", "d" })
        {
            await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(Guid.NewGuid(), $"{tag}-{n}"));
        }

        await _host.InvokeMessageAndWaitAsync(new StartFiDiagSaga(Guid.NewGuid(), "someone-else"));

        var result = await diagnostics.QuerySagasAsync(typeof(FiDiagSaga).FullName!,
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

public record StartFiDiagSaga(Guid Id, string Note);

[WolverineIgnore]
public class FiDiagSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartFiDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}

public record StartFiIntDiagSaga(int Id, string Note);

[WolverineIgnore]
public class FiIntDiagSaga : Wolverine.Saga
{
    public int Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartFiIntDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
