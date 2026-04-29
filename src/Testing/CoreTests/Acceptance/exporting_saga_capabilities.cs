using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// Validates the saga-shape surface added to <see cref="ServiceCapabilities"/>
/// for downstream tools (CritterWatch). Each role classification path
/// (Start / StartOrHandle / Orchestrate / NotFound) plus the cascading
/// PublishedTypes wiring needs end-to-end coverage so a regression on the
/// SagaChain method-name lookup or HandlerChain.PublishedTypes() shows up
/// here, not in a downstream UI bug report.
/// </summary>
public class exporting_saga_capabilities : IAsyncLifetime
{
    private IHost _host = null!;
    private ServiceCapabilities _capabilities = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddResourceSetupOnStartup();
                opts.Discovery.IncludeType<DemoSaga>();
                opts.Discovery.IncludeType<NotFoundOnlySaga>();
            }).StartAsync();

        _capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void emits_one_descriptor_per_saga_state_type()
    {
        _capabilities.Sagas.ShouldNotBeEmpty();
        _capabilities.Sagas
            .Select(s => s.StateType.FullName)
            .ShouldContain(typeof(DemoSaga).FullName!);
    }

    [Fact]
    public void captures_saga_id_type_at_saga_level()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        saga.SagaIdType.ShouldBe(typeof(Guid).FullName!);
    }

    [Fact]
    public void captures_saga_id_member_per_message()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        // Wolverine pulls the saga id from each message's `{SagaName}Id`
        // property by convention. All three of our test messages use
        // `DemoSagaId` so they should all surface that name.
        saga.Messages.ShouldAllBe(m => m.SagaIdMember == nameof(BeginDemoSaga.DemoSagaId));
    }

    [Fact]
    public void classifies_start_handler()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        var role = saga.Messages.Single(m => m.MessageType.FullName == typeof(BeginDemoSaga).FullName!);
        role.Role.ShouldBe(SagaRole.Start);
    }

    [Fact]
    public void classifies_orchestrate_handler()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        var role = saga.Messages.Single(m => m.MessageType.FullName == typeof(AdvanceDemoSaga).FullName!);
        role.Role.ShouldBe(SagaRole.Orchestrate);
    }

    [Fact]
    public void classifies_start_or_handle()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        var role = saga.Messages.Single(m => m.MessageType.FullName == typeof(EnsureDemoSaga).FullName!);
        role.Role.ShouldBe(SagaRole.StartOrHandle);
    }

    [Fact]
    public void classifies_not_found_only_handler()
    {
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(NotFoundOnlySaga).FullName!);
        var role = saga.Messages.Single(m => m.MessageType.FullName == typeof(MissingSagaQuery).FullName!);
        role.Role.ShouldBe(SagaRole.NotFound);
    }

    [Fact]
    public void surfaces_cascading_published_types()
    {
        // Begin* cascades a DemoSagaStarted event from its return tuple.
        var saga = _capabilities.Sagas.Single(s => s.StateType.FullName == typeof(DemoSaga).FullName!);
        var startRole = saga.Messages.Single(m => m.MessageType.FullName == typeof(BeginDemoSaga).FullName!);

        startRole.PublishedTypes
            .Select(t => t.FullName)
            .ShouldContain(typeof(DemoSagaStarted).FullName!);
    }
}

// ---- Test fixtures ----

public record BeginDemoSaga(Guid DemoSagaId);
public record AdvanceDemoSaga(Guid DemoSagaId);
public record EnsureDemoSaga(Guid DemoSagaId);
public record DemoSagaStarted(Guid SagaId);

public class DemoSaga : Saga
{
    public Guid Id { get; set; }

    public DemoSagaStarted Start(BeginDemoSaga cmd)
    {
        Id = cmd.DemoSagaId;
        return new DemoSagaStarted(cmd.DemoSagaId);
    }

    public void Orchestrate(AdvanceDemoSaga cmd)
    {
    }

    public void StartOrHandle(EnsureDemoSaga cmd)
    {
        Id = cmd.DemoSagaId;
    }
}

public record MissingSagaQuery(Guid NotFoundOnlySagaId);

public class NotFoundOnlySaga : Saga
{
    public Guid Id { get; set; }

    // Deliberately no Start/Orchestrate — this saga only defines a NotFound
    // compensating path so the test can assert the NotFound classification
    // works in isolation. NotFound is static per the Wolverine pattern.
    public static void NotFound(MissingSagaQuery query)
    {
    }
}
