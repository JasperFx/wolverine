using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// Validates the saga-shape surface added to <see cref="ServiceCapabilities"/>
/// for downstream tools (CritterWatch). The new
/// <c>SagaTypeDescriptor</c> splits messages into starting vs continuing
/// — Start/StartOrHandle land in StartingMessages, Orchestrate/NotFound in
/// ContinuingMessages, and StartOrHandle additionally lands in continuing
/// because at runtime it can advance an existing saga. Each test below
/// pins one of those classification paths so a regression on the
/// SagaChain method-name lookup shows up here, not in a downstream UI bug
/// report.
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
        _capabilities.SagaTypes.ShouldNotBeEmpty();
        _capabilities.SagaTypes
            .Select(s => s.SagaType.FullName)
            .ShouldContain(typeof(DemoSaga).FullName!);
    }

    [Fact]
    public void start_handler_lands_in_starting_messages()
    {
        var saga = _capabilities.SagaTypes.Single(s => s.SagaType.FullName == typeof(DemoSaga).FullName!);
        saga.StartingMessages
            .Select(m => m.FullName)
            .ShouldContain(typeof(BeginDemoSaga).FullName!);
    }

    [Fact]
    public void orchestrate_handler_lands_in_continuing_messages()
    {
        var saga = _capabilities.SagaTypes.Single(s => s.SagaType.FullName == typeof(DemoSaga).FullName!);
        saga.ContinuingMessages
            .Select(m => m.FullName)
            .ShouldContain(typeof(AdvanceDemoSaga).FullName!);
    }

    [Fact]
    public void start_or_handle_lands_in_both_buckets()
    {
        // StartOrHandle can do either at runtime so the descriptor
        // surfaces it in both starting and continuing — the UI shouldn't
        // have to special-case the role.
        var saga = _capabilities.SagaTypes.Single(s => s.SagaType.FullName == typeof(DemoSaga).FullName!);
        var ensureFullName = typeof(EnsureDemoSaga).FullName!;
        saga.StartingMessages.Select(m => m.FullName).ShouldContain(ensureFullName);
        saga.ContinuingMessages.Select(m => m.FullName).ShouldContain(ensureFullName);
    }

    [Fact]
    public void not_found_only_handler_lands_in_continuing_messages()
    {
        var saga = _capabilities.SagaTypes.Single(s => s.SagaType.FullName == typeof(NotFoundOnlySaga).FullName!);
        saga.ContinuingMessages
            .Select(m => m.FullName)
            .ShouldContain(typeof(MissingSagaQuery).FullName!);
    }

    [Fact]
    public void storage_provider_tag_is_in_memory_when_no_storage_registered()
    {
        // No Marten/EF Core/RavenDB extension is configured for these
        // fixtures, so every saga should land on the InMemory provider —
        // the same one the saga handler pipeline picks at runtime.
        var saga = _capabilities.SagaTypes.Single(s => s.SagaType.FullName == typeof(DemoSaga).FullName!);
        saga.StorageProvider.ShouldBe("InMemory");
    }

    [Fact]
    public void marks_timeout_messages_on_message_descriptor()
    {
        // DemoSagaReminder derives from TimeoutMessage — the descriptor
        // should reflect that so external tools can render saga timeout
        // arrows with a clock affordance instead of a regular handler call.
        var reminder = _capabilities.Messages.Single(m => m.Type.FullName == typeof(DemoSagaReminder).FullName!);
        reminder.IsTimeoutMessage.ShouldBeTrue();

        // Plain commands stay false so the flag is meaningfully discriminating.
        var advance = _capabilities.Messages.Single(m => m.Type.FullName == typeof(AdvanceDemoSaga).FullName!);
        advance.IsTimeoutMessage.ShouldBeFalse();
    }
}

// ---- Test fixtures ----

public record BeginDemoSaga(Guid DemoSagaId);
public record AdvanceDemoSaga(Guid DemoSagaId);
public record EnsureDemoSaga(Guid DemoSagaId);
public record DemoSagaStarted(Guid SagaId);

/// <summary>
/// Saga timeout message — a TimeoutMessage subclass so the
/// MessageDescriptor.IsTimeoutMessage flag has something to assert
/// against. In a real saga this would re-enter the saga after the
/// configured DelayTime to drive a state transition (e.g. "if no payment
/// received within 24h, cancel the booking").
/// </summary>
public record DemoSagaReminder() : TimeoutMessage(TimeSpan.FromMinutes(15));

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

    public void Handle(DemoSagaReminder reminder)
    {
        // No-op: the test only cares that the message type is discovered
        // and surfaces with IsTimeoutMessage = true on the descriptor.
    }
}

public record MissingSagaQuery(Guid NotFoundOnlySagaId);

public class NotFoundOnlySaga : Saga
{
    public Guid Id { get; set; }

    // Deliberately no Start/Orchestrate — this saga only defines a NotFound
    // compensating path so the test can assert that NotFound classification
    // works in isolation. NotFound is static per the Wolverine pattern.
    public static void NotFound(MissingSagaQuery query)
    {
    }
}
