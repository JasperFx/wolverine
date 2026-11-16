using JasperFx.Descriptors;
using JasperFx.Documents;
using Wolverine.Configuration.Capabilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5558: IWolverineRuntime.SagaStorage always implements ISagaIdleDiagnostics and routes each call to the storage
// that owns the saga type; a storage that does not implement the interface is refused, not guessed at
public class saga_idle_diagnostics_routing_5558
{
    private static async Task<IHost> hostWith(params ISagaStoreDiagnostics[] storages)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                foreach (var storage in storages) opts.Services.AddSingleton(storage);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private static SagaDescriptor descriptorFor<T>() =>
        new(TypeDescriptor.For(typeof(T))) { StorageProvider = typeof(T).Name };

    [Fact]
    public async Task routes_idle_calls_to_the_storage_that_owns_the_saga_type()
    {
        var withIdle = new StubIdleSagaStoreDiagnostics(descriptorFor<MartenOwnedSaga>()) { IdleCount = 7 };
        var withoutIdle = new StubSagaStoreDiagnostics("EFCore", descriptorFor<EFOwnedSaga>());

        using var host = await hostWith(withIdle, withoutIdle);
        var idle = host.GetRuntime().SagaStorage.ShouldBeAssignableTo<ISagaIdleDiagnostics>()!;
        var cutoff = DateTimeOffset.UtcNow;

        idle.SupportsIdleQueries(typeof(MartenOwnedSaga).FullName!).ShouldBeTrue();
        idle.SupportsIdleQueries(nameof(MartenOwnedSaga)).ShouldBeTrue();
        (await idle.CountIdleSagasAsync(nameof(MartenOwnedSaga), cutoff, "blue", CancellationToken.None))
            .ShouldBe(7);
        withIdle.CountCalls.ShouldHaveSingleItem().ShouldBe((nameof(MartenOwnedSaga), cutoff, "blue"));

        var range = SagaLastModifiedRange.Before(cutoff);
        await idle.QuerySagasByLastModifiedAsync(nameof(MartenOwnedSaga), range, new DocumentQueryOptions(1, 10),
            CancellationToken.None);
        withIdle.QueryCalls.ShouldHaveSingleItem().ShouldBe(range);
    }

    [Fact]
    public async Task a_storage_without_idle_support_is_refused()
    {
        using var host = await hostWith(new StubSagaStoreDiagnostics("EFCore", descriptorFor<EFOwnedSaga>()));
        var idle = host.GetRuntime().SagaStorage.ShouldBeAssignableTo<ISagaIdleDiagnostics>()!;

        idle.SupportsIdleQueries(nameof(EFOwnedSaga)).ShouldBeFalse();

        await Should.ThrowAsync<NotSupportedException>(() =>
            idle.CountIdleSagasAsync(nameof(EFOwnedSaga), DateTimeOffset.UtcNow, null, CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(() =>
            idle.QuerySagasByLastModifiedAsync(nameof(EFOwnedSaga), SagaLastModifiedRange.Any,
                new DocumentQueryOptions(1, 10), CancellationToken.None));
    }

    [Fact]
    public async Task a_saga_type_no_storage_owns_counts_zero()
    {
        using var host = await hostWith(new StubIdleSagaStoreDiagnostics(descriptorFor<MartenOwnedSaga>()));
        var idle = host.GetRuntime().SagaStorage.ShouldBeAssignableTo<ISagaIdleDiagnostics>()!;

        idle.SupportsIdleQueries("Nobody").ShouldBeFalse();
        (await idle.CountIdleSagasAsync("Nobody", DateTimeOffset.UtcNow, null, CancellationToken.None)).ShouldBe(0);

        var result = await idle.QuerySagasByLastModifiedAsync("Nobody", SagaLastModifiedRange.Any,
            new DocumentQueryOptions(2, 5), CancellationToken.None);
        result.Instances.ShouldBeEmpty();
        result.PageNumber.ShouldBe(2);
        result.PageSize.ShouldBe(5);
    }

    [Fact]
    public void range_helpers()
    {
        var cutoff = DateTimeOffset.UtcNow;
        SagaLastModifiedRange.Before(cutoff).ShouldBe(new SagaLastModifiedRange(null, cutoff));
        SagaLastModifiedRange.After(cutoff).ShouldBe(new SagaLastModifiedRange(cutoff, null));
        SagaLastModifiedRange.Any.ShouldBe(new SagaLastModifiedRange());
    }

    [Fact]
    public void the_tenant_is_split_off_the_options_and_normalized()
    {
        var (tenantId, rest) = SagaQueryCriteria.TakeTenant(new DocumentQueryOptions(1, 10) { TenantId = "blue" });
        tenantId.ShouldBe("blue");
        rest.TenantId.ShouldBeNull();

        SagaQueryCriteria.TakeTenant(new DocumentQueryOptions(1, 10)).TenantId.ShouldBeNull();

        Should.NotThrow(() => SagaQueryCriteria.RefuseTenant(null, "no tenants"));
        Should.Throw<DocumentCriteriaNotSupportedException>(() => SagaQueryCriteria.RefuseTenant("blue", "no tenants"));
    }
}

internal sealed class StubIdleSagaStoreDiagnostics : ISagaStoreDiagnostics, ISagaIdleDiagnostics
{
    private readonly SagaDescriptor[] _descriptors;

    public StubIdleSagaStoreDiagnostics(params SagaDescriptor[] descriptors)
    {
        _descriptors = descriptors;
    }

    public long IdleCount { get; init; }
    public List<(string, DateTimeOffset, string?)> CountCalls { get; } = new();
    public List<SagaLastModifiedRange> QueryCalls { get; } = new();

    private bool owns(string name) =>
        _descriptors.Any(x => x.StateType.FullName == name || x.StateType.Name == name);

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SagaDescriptor>>(_descriptors);

    public Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
        => Task.FromResult<SagaInstanceState?>(null);

    public Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SagaInstanceState>>(Array.Empty<SagaInstanceState>());

    public bool SupportsIdleQueries(string sagaTypeName) => owns(sagaTypeName);

    public Task<long> CountIdleSagasAsync(string sagaTypeName, DateTimeOffset modifiedBefore, string? tenantId,
        CancellationToken ct)
    {
        CountCalls.Add((sagaTypeName, modifiedBefore, tenantId));
        return Task.FromResult(IdleCount);
    }

    public Task<SagaQueryResult> QuerySagasByLastModifiedAsync(string sagaTypeName, SagaLastModifiedRange range,
        DocumentQueryOptions options, CancellationToken ct)
    {
        QueryCalls.Add(range);
        return Task.FromResult(SagaQueryResult.Empty(options.PageNumber, options.PageSize));
    }
}
