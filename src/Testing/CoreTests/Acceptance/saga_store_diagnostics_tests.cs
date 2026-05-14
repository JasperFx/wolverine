using System.Text.Json;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// Aggregator-level tests for <see cref="ISagaStoreDiagnostics"/>
/// surfacing through <see cref="IWolverineRuntime.SagaStorage"/>.
/// Drives the runtime with stub <c>ISagaStoreDiagnostics</c> registrations
/// instead of standing up a real Marten / EF Core / RavenDB host —
/// every routing path the aggregator owns can be exercised this way
/// without paying database setup cost on every CI run. The
/// storage-specific implementations get their own integration tests
/// where a real backing store is required.
/// </summary>
public class saga_store_diagnostics_tests
{
    [Fact]
    public async Task aggregator_concatenates_all_registered_storages()
    {
        // Two saga storages, each owning a different saga type — the
        // multi-storage shape CritterWatch will see when a host wires
        // Marten alongside EF Core.
        var martenStub = new StubSagaStoreDiagnostics(
            "Marten",
            new SagaDescriptor(TypeDescriptor.For(typeof(MartenOwnedSaga))) { StorageProvider = "Marten" });
        var efStub = new StubSagaStoreDiagnostics(
            "EFCore",
            new SagaDescriptor(TypeDescriptor.For(typeof(EFOwnedSaga))) { StorageProvider = "EFCore" });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<ISagaStoreDiagnostics>(martenStub);
                opts.Services.AddSingleton<ISagaStoreDiagnostics>(efStub);
            })
            .StartAsync();

        var diagnostics = host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        registered.Select(d => d.StateType.FullName)
            .ShouldBe(new[] { typeof(MartenOwnedSaga).FullName!, typeof(EFOwnedSaga).FullName! }, ignoreOrder: true);
    }

    [Fact]
    public async Task aggregator_routes_read_to_correct_storage()
    {
        // Each stub records the saga-type-names it's asked for so we
        // can assert the aggregator dispatched the right call to the
        // right storage rather than fanning out to both.
        var martenStub = new StubSagaStoreDiagnostics(
            "Marten",
            new SagaDescriptor(TypeDescriptor.For(typeof(MartenOwnedSaga))) { StorageProvider = "Marten" })
        {
            ReadResults =
            {
                [typeof(MartenOwnedSaga).FullName!] = new SagaInstanceState(
                    typeof(MartenOwnedSaga).FullName!,
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    false,
                    JsonSerializer.SerializeToElement(new { from = "marten" }),
                    null)
            }
        };
        var efStub = new StubSagaStoreDiagnostics(
            "EFCore",
            new SagaDescriptor(TypeDescriptor.For(typeof(EFOwnedSaga))) { StorageProvider = "EFCore" })
        {
            ReadResults =
            {
                [typeof(EFOwnedSaga).FullName!] = new SagaInstanceState(
                    typeof(EFOwnedSaga).FullName!,
                    42,
                    false,
                    JsonSerializer.SerializeToElement(new { from = "efcore" }),
                    null)
            }
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<ISagaStoreDiagnostics>(martenStub);
                opts.Services.AddSingleton<ISagaStoreDiagnostics>(efStub);
            })
            .StartAsync();

        var diagnostics = host.GetRuntime().SagaStorage;

        var martenSaga = await diagnostics.ReadSagaAsync(
            typeof(MartenOwnedSaga).FullName!,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CancellationToken.None);
        martenSaga.ShouldNotBeNull();
        martenSaga.State.GetProperty("from").GetString().ShouldBe("marten");

        var efSaga = await diagnostics.ReadSagaAsync(
            typeof(EFOwnedSaga).FullName!,
            42,
            CancellationToken.None);
        efSaga.ShouldNotBeNull();
        efSaga.State.GetProperty("from").GetString().ShouldBe("efcore");

        // Each storage was only asked for its own saga type — no
        // cross-storage fan-out for read calls.
        martenStub.ReadCalls.ShouldHaveSingleItem().ShouldBe(typeof(MartenOwnedSaga).FullName!);
        efStub.ReadCalls.ShouldHaveSingleItem().ShouldBe(typeof(EFOwnedSaga).FullName!);
    }

    [Fact]
    public async Task aggregator_returns_null_for_unknown_saga_type()
    {
        var stub = new StubSagaStoreDiagnostics(
            "Marten",
            new SagaDescriptor(TypeDescriptor.For(typeof(MartenOwnedSaga))) { StorageProvider = "Marten" });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Services.AddSingleton<ISagaStoreDiagnostics>(stub))
            .StartAsync();

        var diagnostics = host.GetRuntime().SagaStorage;

        var result = await diagnostics.ReadSagaAsync(
            "Some.Unknown.Saga, Wherever",
            Guid.NewGuid(),
            CancellationToken.None);

        result.ShouldBeNull();
        // The aggregator must NOT delegate unknown-type reads to any
        // storage — that would force every storage to do its own
        // unknown-type bookkeeping. Verify we never asked the stub.
        stub.ReadCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task aggregator_returns_empty_list_for_unknown_saga_type()
    {
        var stub = new StubSagaStoreDiagnostics(
            "Marten",
            new SagaDescriptor(TypeDescriptor.For(typeof(MartenOwnedSaga))) { StorageProvider = "Marten" });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Services.AddSingleton<ISagaStoreDiagnostics>(stub))
            .StartAsync();

        var list = await host.GetRuntime().SagaStorage.ListSagaInstancesAsync(
            "Some.Unknown.Saga, Wherever", 10, CancellationToken.None);

        list.ShouldBeEmpty();
        stub.ListCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task aggregator_routes_by_short_name_or_full_name()
    {
        var stub = new StubSagaStoreDiagnostics(
            "Marten",
            new SagaDescriptor(TypeDescriptor.For(typeof(MartenOwnedSaga))) { StorageProvider = "Marten" })
        {
            ReadResults =
            {
                [typeof(MartenOwnedSaga).FullName!] = new SagaInstanceState(
                    typeof(MartenOwnedSaga).FullName!,
                    Guid.Empty,
                    false,
                    JsonSerializer.SerializeToElement(new { from = "marten" }),
                    null),
                [nameof(MartenOwnedSaga)] = new SagaInstanceState(
                    typeof(MartenOwnedSaga).FullName!,
                    Guid.Empty,
                    false,
                    JsonSerializer.SerializeToElement(new { from = "marten-by-short" }),
                    null)
            }
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Services.AddSingleton<ISagaStoreDiagnostics>(stub))
            .StartAsync();

        var diagnostics = host.GetRuntime().SagaStorage;

        // Either form should resolve. The aggregator indexes by both
        // FullName and Name so CritterWatch can route on whichever the
        // operator typed.
        var byFull = await diagnostics.ReadSagaAsync(typeof(MartenOwnedSaga).FullName!, Guid.Empty, CancellationToken.None);
        var byShort = await diagnostics.ReadSagaAsync(nameof(MartenOwnedSaga), Guid.Empty, CancellationToken.None);

        byFull.ShouldNotBeNull();
        byShort.ShouldNotBeNull();
    }

    [Fact]
    public async Task no_storages_registered_returns_empty_catalog()
    {
        // When no host registers an ISagaStoreDiagnostics, the runtime
        // still exposes one — an empty aggregator. This keeps callers
        // from having to null-check IWolverineRuntime.SagaStorage.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(_ => { })
            .StartAsync();

        var diagnostics = host.GetRuntime().SagaStorage;
        diagnostics.ShouldNotBeNull();

        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);
        registered.ShouldBeEmpty();

        var read = await diagnostics.ReadSagaAsync("Anything", Guid.NewGuid(), CancellationToken.None);
        read.ShouldBeNull();
    }
}

/// <summary>
/// Saga-type-and-storage matrix tests for
/// <see cref="Wolverine.Configuration.Capabilities.ServiceCapabilities.Sagas"/>.
/// Pinned separately from the start/continue classification tests
/// (those live in <c>exporting_saga_capabilities</c>) so a regression
/// on the StorageProvider tag — the field downstream tools group by —
/// surfaces with a focused failure rather than as part of a sprawling
/// fixture.
/// </summary>
public class service_capabilities_saga_types_tests
{
    [Fact]
    public async Task saga_types_collection_is_empty_when_no_sagas()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Bare-bones discovery — must not pick up the dozens of
                // saga fixtures sitting in CoreTests. We use this to
                // validate that an app with literally zero saga
                // handlers reports an empty Sagas collection rather
                // than blowing up.
                opts.Discovery.DisableConventionalDiscovery();
            })
            .StartAsync();

        var capabilities = await Wolverine.Configuration.Capabilities.ServiceCapabilities.ReadFrom(
            host.GetRuntime(), null, CancellationToken.None);

        capabilities.Sagas.ShouldBeEmpty();
    }
}

// ---- Test fixtures ----

public class MartenOwnedSaga : Saga
{
    public Guid Id { get; set; }
    public void Start(StartMartenSaga _) { }
}

public class EFOwnedSaga : Saga
{
    public int Id { get; set; }
    public void Start(StartEFSaga _) { }
}

public record StartMartenSaga(Guid Id);
public record StartEFSaga(int Id);

internal sealed class StubSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly SagaDescriptor[] _descriptors;

    public StubSagaStoreDiagnostics(string label, params SagaDescriptor[] descriptors)
    {
        Label = label;
        _descriptors = descriptors;
    }

    public string Label { get; }
    public List<string> ReadCalls { get; } = new();
    public List<string> ListCalls { get; } = new();
    public Dictionary<string, SagaInstanceState> ReadResults { get; } = new();
    public Dictionary<string, IReadOnlyList<SagaInstanceState>> ListResults { get; } = new();

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SagaDescriptor>>(_descriptors);

    public Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        ReadCalls.Add(sagaTypeName);
        return Task.FromResult(ReadResults.TryGetValue(sagaTypeName, out var s) ? s : null);
    }

    public Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        ListCalls.Add(sagaTypeName);
        return Task.FromResult(ListResults.TryGetValue(sagaTypeName, out var s) ? s : Array.Empty<SagaInstanceState>());
    }
}
