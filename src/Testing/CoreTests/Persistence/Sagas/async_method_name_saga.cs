using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports;
using JasperFx.Core.Reflection;
using Xunit;

namespace CoreTests.Persistence.Sagas;

/// <summary>
/// Comprehensive regression coverage for https://github.com/JasperFx/wolverine/issues/3274
/// (a re-report of https://github.com/JasperFx/wolverine/issues/2578).
///
/// <para>
/// This is an async-method clone of <c>GuidIdentifiedSagaComplianceSpecs</c> /
/// <c>BasicWorkflow</c>. Every meaningful <see cref="Saga"/> method convention is
/// exercised here using the conventional <c>Async</c> suffix and a genuinely
/// asynchronous (<c>Task</c>-returning) body. <c>HandlerDiscovery</c> strips the
/// <c>Async</c> suffix when it picks up handler methods, but
/// <c>SagaChain.findByNames</c> used to match the bare name with strict equality,
/// so async-suffixed saga methods were discovered into the handler graph yet
/// silently dropped from <c>StartingCalls</c> / <c>ExistingCalls</c> /
/// <c>NotFoundCalls</c> — the saga never started and cascaded continuations were
/// parked forever with no error.
/// </para>
///
/// <para>
/// The methods covered (each in both discovery-level and end-to-end form):
/// <c>StartAsync</c>, <c>StartsAsync</c>, <c>StartOrHandleAsync</c>,
/// <c>StartsOrHandlesAsync</c>, <c>HandleAsync</c>, <c>HandlesAsync</c>,
/// <c>ConsumeAsync</c>, <c>ConsumesAsync</c>, <c>OrchestrateAsync</c>,
/// <c>OrchestratesAsync</c>, and <c>NotFoundAsync</c>.
/// </para>
/// </summary>
public class async_method_name_saga : IAsyncLifetime
{
    private readonly Guid stateId = Guid.NewGuid();
    private IHost _host = null!;
    private AsyncSagaProbe _probe = null!;

    public async Task InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.DisableConventionalDiscovery().IncludeType<AsyncWorkflow>();
            opts.Services.AddSingleton<AsyncSagaProbe>();
            opts.PublishAllMessages().To(TransportConstants.LocalUri);
        });

        _probe = _host.Services.GetRequiredService<AsyncSagaProbe>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // ---------- harness helpers (mirrors SagaTestHarness) ----------

    private Task send<TMessage>(TMessage message)
    {
        return _host.ExecuteAndWaitValueTaskAsync(x => x.SendAsync(message!));
    }

    private Task send<TMessage>(TMessage message, object sagaId)
    {
        return _host.SendMessageAndWaitAsync(message, new DeliveryOptions { SagaId = sagaId.ToString() }, 10000);
    }

    private Task invoke<TMessage>(TMessage message)
    {
        return _host.InvokeMessageAndWaitAsync(message!);
    }

    private AsyncWorkflow? LoadState(Guid id)
    {
        return _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<AsyncWorkflow>(id);
    }

    private Task<string> codeFor<TMessage>()
    {
        return Task.FromResult(_host.Get<HandlerGraph>().HandlerFor<TMessage>()!.As<MessageHandler>().Chain!.SourceCode!);
    }

    private SagaChain ChainFor<TMessage>()
    {
        return (SagaChain)_host.Services.GetRequiredService<HandlerGraph>()
            .HandlerFor<TMessage>()!.As<MessageHandler>().Chain!;
    }

    // ---------- discovery-level assertions: every method name lands in the right bucket ----------

    [Fact]
    public void StartAsync_is_a_StartingCall()
        => ChainFor<AsyncStart>().StartingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartAsync));

    [Fact]
    public void StartsAsync_is_a_StartingCall()
        => ChainFor<AsyncWildcardStart>().StartingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartsAsync));

    [Fact]
    public void StartOrHandleAsync_is_in_both_StartingCalls_and_ExistingCalls()
    {
        ChainFor<AsyncStartOrHandle>().StartingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartOrHandleAsync));
        ChainFor<AsyncStartOrHandle>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartOrHandleAsync));
    }

    [Fact]
    public void StartsOrHandlesAsync_is_in_both_StartingCalls_and_ExistingCalls()
    {
        ChainFor<AsyncStartsOrHandles>().StartingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartsOrHandlesAsync));
        ChainFor<AsyncStartsOrHandles>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.StartsOrHandlesAsync));
    }

    [Fact]
    public void HandleAsync_is_an_ExistingCall()
        => ChainFor<AsyncCompleteOne>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.HandleAsync));

    [Fact]
    public void HandlesAsync_is_an_ExistingCall()
        => ChainFor<AsyncCompleteTwo>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.HandlesAsync));

    [Fact]
    public void ConsumeAsync_is_an_ExistingCall()
        => ChainFor<AsyncConsumeOne>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.ConsumeAsync));

    [Fact]
    public void ConsumesAsync_is_an_ExistingCall()
        => ChainFor<AsyncConsumeTwo>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.ConsumesAsync));

    [Fact]
    public void OrchestrateAsync_is_an_ExistingCall()
        => ChainFor<AsyncOrchestrateOne>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.OrchestrateAsync));

    [Fact]
    public void OrchestratesAsync_is_an_ExistingCall()
        => ChainFor<AsyncOrchestrateTwo>().ExistingCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.OrchestratesAsync));

    [Fact]
    public void NotFoundAsync_is_a_NotFoundCall()
        => ChainFor<AsyncMaybeOrphan>().NotFoundCalls.ShouldContain(x => x.Method.Name == nameof(AsyncWorkflow.NotFoundAsync));

    // ---------- end-to-end assertions cloned from GuidIdentifiedSagaComplianceSpecs ----------

    [Fact]
    public async Task start_1_with_StartAsync()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });

        var state = LoadState(stateId);
        state.ShouldNotBeNull();
        state.Name.ShouldBe("Croaker");
    }

    [Fact]
    public async Task start_2_with_StartsAsync()
    {
        await send(new AsyncWildcardStart { Id = stateId.ToString(), Name = "One Eye" });

        var state = LoadState(stateId);
        state.ShouldNotBeNull();
        state.Name.ShouldBe("One Eye");
    }

    [Fact]
    public async Task complete_and_delete_with_HandleAsync()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncFinishItAll(), stateId);

        LoadState(stateId).ShouldBeNull();
    }

    [Fact]
    public async Task cascading_message_passes_along_the_saga_id_in_header()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });

        Debug.WriteLine(await codeFor<AsyncStart>());
        Debug.WriteLine(await codeFor<AsyncCompleteOne>());

        // HandleAsync(AsyncCompleteOne) returns AsyncCompleteTwo, which HandlesAsync handles.
        await send(new AsyncCompleteOne { SagaId = stateId });

        var state = LoadState(stateId);
        state!.OneCompleted.ShouldBeTrue();
        state.TwoCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task straight_up_update_with_the_saga_id_on_the_message()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncCompleteThree { SagaId = stateId });

        LoadState(stateId)!.ThreeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_with_message_that_uses_saga_identity_attributed_property()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncDoThree { TheSagaId = stateId });

        LoadState(stateId)!.DoThreeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_expecting_the_saga_id_to_be_on_the_envelope()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncCompleteFour(), stateId);

        LoadState(stateId)!.FourCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task update_with_no_saga_id_to_be_on_the_envelope()
    {
        await Should.ThrowAsync<IndeterminateSagaStateIdException>(async () => { await invoke(new AsyncCompleteFour()); });
    }

    [Fact]
    public async Task OrchestrateAsync_is_invoked_against_existing_saga()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncOrchestrateOne { SagaId = stateId });

        LoadState(stateId)!.OrchestrateCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task OrchestratesAsync_is_invoked_against_existing_saga()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncOrchestrateTwo { SagaId = stateId });

        LoadState(stateId)!.OrchestratesCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumeAsync_is_invoked_against_existing_saga()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncConsumeOne { SagaId = stateId });

        LoadState(stateId)!.ConsumeCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumesAsync_is_invoked_against_existing_saga()
    {
        await send(new AsyncStart { Id = stateId, Name = "Croaker" });
        await send(new AsyncConsumeTwo { SagaId = stateId });

        LoadState(stateId)!.ConsumesCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task StartOrHandleAsync_starts_then_handles()
    {
        // First message starts the saga...
        await send(new AsyncStartOrHandle { Id = stateId });
        var state = LoadState(stateId);
        state.ShouldNotBeNull();
        state.StartOrHandleCount.ShouldBe(1);

        // ...the second is routed to the now-existing saga.
        await send(new AsyncStartOrHandle { Id = stateId });
        LoadState(stateId)!.StartOrHandleCount.ShouldBe(2);
    }

    [Fact]
    public async Task StartsOrHandlesAsync_starts_then_handles()
    {
        await send(new AsyncStartsOrHandles { Id = stateId });
        LoadState(stateId)!.StartsOrHandlesCount.ShouldBe(1);

        await send(new AsyncStartsOrHandles { Id = stateId });
        LoadState(stateId)!.StartsOrHandlesCount.ShouldBe(2);
    }

    [Fact]
    public async Task NotFoundAsync_is_invoked_when_the_saga_does_not_exist()
    {
        // No saga exists for this id, and there is no Start handler for AsyncMaybeOrphan,
        // so NotFoundAsync must run instead of throwing the "saga not found" exception.
        await send(new AsyncMaybeOrphan { SagaId = stateId });

        _probe.NotFoundInvoked.ShouldBeTrue();
        LoadState(stateId).ShouldBeNull();
    }
}

#region Test sagas and messages

/// <summary>Singleton used to observe a static <c>NotFoundAsync</c> being invoked.</summary>
public class AsyncSagaProbe
{
    public bool NotFoundInvoked { get; set; }
}

public class AsyncStart
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
}

public class AsyncWildcardStart
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
}

public class AsyncStartOrHandle
{
    public Guid Id { get; set; }
}

public class AsyncStartsOrHandles
{
    public Guid Id { get; set; }
}

public class AsyncCompleteOne
{
    public Guid SagaId { get; set; }
}

// Deliberately carries no id member: the saga id rides on the envelope header
// when this message is cascaded out of HandleAsync(AsyncCompleteOne).
public class AsyncCompleteTwo;

public class AsyncCompleteThree
{
    public Guid SagaId { get; set; }
}

public class AsyncDoThree
{
    [SagaIdentity] public Guid TheSagaId { get; set; }
}

public class AsyncCompleteFour;

public class AsyncConsumeOne
{
    public Guid SagaId { get; set; }
}

public class AsyncConsumeTwo
{
    public Guid SagaId { get; set; }
}

public class AsyncOrchestrateOne
{
    public Guid SagaId { get; set; }
}

public class AsyncOrchestrateTwo
{
    public Guid SagaId { get; set; }
}

public class AsyncFinishItAll;

public class AsyncMaybeOrphan
{
    public Guid SagaId { get; set; }
}

[WolverineIgnore]
public class AsyncWorkflow : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;

    public bool OneCompleted { get; set; }
    public bool TwoCompleted { get; set; }
    public bool ThreeCompleted { get; set; }
    public bool DoThreeCompleted { get; set; }
    public bool FourCompleted { get; set; }
    public bool OrchestrateCompleted { get; set; }
    public bool OrchestratesCompleted { get; set; }
    public bool ConsumeCompleted { get; set; }
    public bool ConsumesCompleted { get; set; }
    public int StartOrHandleCount { get; set; }
    public int StartsOrHandlesCount { get; set; }

    public Task StartAsync(AsyncStart starting)
    {
        Id = starting.Id;
        Name = starting.Name;
        return Task.CompletedTask;
    }

    public Task StartsAsync(AsyncWildcardStart starting)
    {
        Id = Guid.Parse(starting.Id);
        Name = starting.Name;
        return Task.CompletedTask;
    }

    public Task StartOrHandleAsync(AsyncStartOrHandle message)
    {
        Id = message.Id;
        StartOrHandleCount++;
        return Task.CompletedTask;
    }

    public Task StartsOrHandlesAsync(AsyncStartsOrHandles message)
    {
        Id = message.Id;
        StartsOrHandlesCount++;
        return Task.CompletedTask;
    }

    public Task<AsyncCompleteTwo> HandleAsync(AsyncCompleteOne one)
    {
        OneCompleted = true;
        return Task.FromResult(new AsyncCompleteTwo());
    }

    public Task HandlesAsync(AsyncCompleteTwo message)
    {
        TwoCompleted = true;
        return Task.CompletedTask;
    }

    public Task HandleAsync(AsyncCompleteThree three)
    {
        ThreeCompleted = true;
        return Task.CompletedTask;
    }

    public Task HandlesAsync(AsyncDoThree three)
    {
        DoThreeCompleted = true;
        return Task.CompletedTask;
    }

    public Task HandleAsync(AsyncCompleteFour four)
    {
        FourCompleted = true;
        return Task.CompletedTask;
    }

    public Task OrchestrateAsync(AsyncOrchestrateOne message)
    {
        OrchestrateCompleted = true;
        return Task.CompletedTask;
    }

    public Task OrchestratesAsync(AsyncOrchestrateTwo message)
    {
        OrchestratesCompleted = true;
        return Task.CompletedTask;
    }

    public Task ConsumeAsync(AsyncConsumeOne message)
    {
        ConsumeCompleted = true;
        return Task.CompletedTask;
    }

    public Task ConsumesAsync(AsyncConsumeTwo message)
    {
        ConsumesCompleted = true;
        return Task.CompletedTask;
    }

    public Task HandleAsync(AsyncFinishItAll finish)
    {
        MarkCompleted();
        return Task.CompletedTask;
    }

    public Task HandleAsync(AsyncMaybeOrphan message)
    {
        // Existing-saga path; only reached when the saga already exists.
        return Task.CompletedTask;
    }

    public static Task NotFoundAsync(AsyncMaybeOrphan message, AsyncSagaProbe probe)
    {
        probe.NotFoundInvoked = true;
        return Task.CompletedTask;
    }
}

#endregion
