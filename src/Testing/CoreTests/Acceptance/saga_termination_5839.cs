using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-5839. <see cref="ISagaStoreDiagnostics.CompleteSagaAsync" /> through <c>IWolverineRuntime.SagaStorage</c>: it
/// routes to the storage that owns the saga type, and a storage written before completion existed still loads and
/// refuses it. Each storage's own behaviour is pinned by <c>SagaTerminationCompliance</c> in its test project.
/// </summary>
public class saga_termination_5839
{
    private static SagaDescriptor describe<T>(string storage)
        => new(TypeDescriptor.For(typeof(T))) { StorageProvider = storage };

    private static async Task<IHost> hostWith(params ISagaStoreDiagnostics[] storages)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                foreach (var storage in storages) opts.Services.AddSingleton(storage);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task completion_routes_to_the_storage_that_owns_the_saga_type()
    {
        var marten = new CompletingSagaStoreDiagnostics(describe<MartenOwnedSaga>("Marten"));
        var ef = new CompletingSagaStoreDiagnostics(describe<EFOwnedSaga>("EFCore"));
        using var host = await hostWith(marten, ef);

        var sagas = host.GetRuntime().SagaStorage;
        var result = await sagas.CompleteSagaAsync(typeof(EFOwnedSaga).FullName!, 7, "never going to finish",
            TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(SagaTerminationOutcome.Deleted);
        ef.Completed.ShouldBe([(typeof(EFOwnedSaga).FullName!, (object)7, "never going to finish")]);
        marten.Completed.ShouldBeEmpty();
    }

    [Fact]
    public async Task completion_routes_by_the_short_type_name_too()
    {
        var ef = new CompletingSagaStoreDiagnostics(describe<EFOwnedSaga>("EFCore"));
        using var host = await hostWith(ef);

        await host.GetRuntime().SagaStorage.CompleteSagaAsync(nameof(EFOwnedSaga), 7, "stuck",
            TestContext.Current.CancellationToken);

        ef.Completed.Single().SagaTypeName.ShouldBe(nameof(EFOwnedSaga));
    }

    [Fact]
    public async Task completion_is_offered_only_where_the_owning_storage_supports_it()
    {
        var old = new StubSagaStoreDiagnostics("Marten", describe<MartenOwnedSaga>("Marten"));
        var ef = new CompletingSagaStoreDiagnostics(describe<EFOwnedSaga>("EFCore"));
        using var host = await hostWith(old, ef);

        var sagas = host.GetRuntime().SagaStorage;
        sagas.SupportsSagaCompletion(typeof(EFOwnedSaga).FullName!).ShouldBeTrue();
        sagas.SupportsSagaCompletion(typeof(MartenOwnedSaga).FullName!).ShouldBeFalse();
        sagas.SupportsSagaCompletion("No.Such.Saga").ShouldBeFalse();
    }

    [Fact]
    public async Task a_storage_written_before_completion_existed_refuses_it()
    {
        // StubSagaStoreDiagnostics implements none of the GH-5839 members, so it gets the interface's defaults
        var old = new StubSagaStoreDiagnostics("Marten", describe<MartenOwnedSaga>("Marten"));
        using var host = await hostWith(old);

        ((ISagaStoreDiagnostics)old).SupportsSagaCompletion(typeof(MartenOwnedSaga).FullName!).ShouldBeFalse();

        await Should.ThrowAsync<NotSupportedException>(() => host.GetRuntime().SagaStorage.CompleteSagaAsync(
            typeof(MartenOwnedSaga).FullName!, Guid.NewGuid(), "stuck", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task an_unknown_saga_type_is_refused()
    {
        var ef = new CompletingSagaStoreDiagnostics(describe<EFOwnedSaga>("EFCore"));
        using var host = await hostWith(ef);

        await Should.ThrowAsync<NotSupportedException>(() => host.GetRuntime().SagaStorage.CompleteSagaAsync(
            "No.Such.Saga", 7, "stuck", TestContext.Current.CancellationToken));
        ef.Completed.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task a_reason_is_required(string reason)
    {
        var ef = new CompletingSagaStoreDiagnostics(describe<EFOwnedSaga>("EFCore"));
        using var host = await hostWith(ef);

        await Should.ThrowAsync<ArgumentException>(() => host.GetRuntime().SagaStorage.CompleteSagaAsync(
            typeof(EFOwnedSaga).FullName!, 7, reason, TestContext.Current.CancellationToken));
        ef.Completed.ShouldBeEmpty();
    }

    [Fact]
    public void the_outcomes_that_end_the_saga()
    {
        SagaTerminationResult result(SagaTerminationOutcome outcome) => new("Saga", 1, outcome);

        result(SagaTerminationOutcome.Deleted).Terminated.ShouldBeTrue();
        result(SagaTerminationOutcome.Retained).Terminated.ShouldBeTrue();
        result(SagaTerminationOutcome.CompletionAppended).Terminated.ShouldBeTrue();
        result(SagaTerminationOutcome.AlreadyCompleted).Terminated.ShouldBeFalse();
        result(SagaTerminationOutcome.NotFound).Terminated.ShouldBeFalse();
    }
}

internal sealed class CompletingSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly SagaDescriptor[] _descriptors;

    public CompletingSagaStoreDiagnostics(params SagaDescriptor[] descriptors)
    {
        _descriptors = descriptors;
    }

    public List<(string SagaTypeName, object Identity, string Reason)> Completed { get; } = new();

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SagaDescriptor>>(_descriptors);

    public Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
        => Task.FromResult<SagaInstanceState?>(null);

    public Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SagaInstanceState>>(Array.Empty<SagaInstanceState>());

    public bool SupportsSagaCompletion(string sagaTypeName)
        => _descriptors.Any(x => x.StateType.FullName == sagaTypeName || x.StateType.Name == sagaTypeName);

    public Task<SagaTerminationResult> CompleteSagaAsync(string sagaTypeName, object identity, string reason,
        CancellationToken ct)
    {
        Completed.Add((sagaTypeName, identity, reason));
        return Task.FromResult(new SagaTerminationResult(sagaTypeName, identity, SagaTerminationOutcome.Deleted));
    }
}
