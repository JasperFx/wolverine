using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Regression coverage for https://github.com/JasperFx/wolverine/issues/2578.
///
/// HandlerDiscovery already accepts <c>StartAsync</c> / <c>HandleAsync</c> /
/// <c>OrchestrateAsync</c> / <c>ConsumeAsync</c> / <c>NotFoundAsync</c> as
/// valid handler method names (it strips the "Async" suffix when matching).
/// But <c>SagaChain.findByNames</c> previously matched on strict equality, so
/// async-suffixed saga methods were discovered into the handler graph yet
/// silently dropped from <c>StartingCalls</c> / <c>ExistingCalls</c> /
/// <c>NotFoundCalls</c>. The generated handler then constructed the saga but
/// never invoked the user's method, leaving <c>Saga.Id == Guid.Empty</c> and
/// throwing on insert.
///
/// These tests verify that all forms of saga method are now discovered AND
/// invoked when their names carry the <c>Async</c> suffix.
/// </summary>
public class Bug_2578_saga_async_method_names : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<AsyncMethodSaga>()
                    .IncludeType<AsyncOrchestrateSaga>()
                    .IncludeType<AsyncStartOrHandleSaga>()
                    .IncludeType<AsyncConsumeSaga>()
                    .IncludeType<AsyncNotFoundSaga>();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private SagaChain ChainFor<TMessage>()
    {
        return (SagaChain)_host.Services.GetRequiredService<HandlerGraph>()
            .HandlerFor<TMessage>()!.As<MessageHandler>().Chain!;
    }

    // ---------- Discovery-level assertions ----------

    [Fact]
    public void StartAsync_is_picked_up_as_a_StartingCall()
    {
        var chain = ChainFor<StartAsyncCommand2578>();
        chain.StartingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncMethodSaga.StartAsync));
    }

    [Fact]
    public void HandleAsync_is_picked_up_as_an_ExistingCall()
    {
        var chain = ChainFor<HandleAsyncCommand2578>();
        chain.ExistingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncMethodSaga.HandleAsync));
    }

    [Fact]
    public void OrchestrateAsync_is_picked_up_as_an_ExistingCall()
    {
        var chain = ChainFor<OrchestrateAsyncCommand2578>();
        chain.ExistingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncOrchestrateSaga.OrchestrateAsync));
    }

    [Fact]
    public void ConsumeAsync_is_picked_up_as_an_ExistingCall()
    {
        var chain = ChainFor<ConsumeAsyncCommand2578>();
        chain.ExistingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncConsumeSaga.ConsumeAsync));
    }

    [Fact]
    public void StartOrHandleAsync_is_picked_up_in_both_StartingCalls_and_ExistingCalls()
    {
        var chain = ChainFor<StartOrHandleAsyncCommand2578>();
        chain.StartingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncStartOrHandleSaga.StartOrHandleAsync));
        chain.ExistingCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncStartOrHandleSaga.StartOrHandleAsync));
    }

    [Fact]
    public void NotFoundAsync_is_picked_up_as_a_NotFoundCall()
    {
        var chain = ChainFor<NotFoundAsyncCommand2578>();
        chain.NotFoundCalls.ShouldHaveSingleItem()
            .Method.Name.ShouldBe(nameof(AsyncNotFoundSaga.NotFoundAsync));
    }

    // ---------- End-to-end assertions ----------

    [Fact]
    public async Task StartAsync_is_actually_invoked_and_persists_the_saga()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartAsyncCommand2578(id, "first"));

        var saga = _host.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<AsyncMethodSaga>(id);
        saga.ShouldNotBeNull("StartAsync must be invoked by the generated handler");
        saga.Id.ShouldBe(id);
        saga.Name.ShouldBe("first");
    }

    [Fact]
    public async Task HandleAsync_is_actually_invoked_against_an_existing_saga()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartAsyncCommand2578(id, "first"));
        await _host.InvokeMessageAndWaitAsync(new HandleAsyncCommand2578 { SagaId = id, NextName = "second" });

        var saga = _host.Services.GetRequiredService<InMemorySagaPersistor>()
            .Load<AsyncMethodSaga>(id);
        saga.ShouldNotBeNull();
        saga.Name.ShouldBe("second");
    }
}

#region Test sagas

public record StartAsyncCommand2578(Guid Id, string Name);

public class HandleAsyncCommand2578
{
    public Guid SagaId { get; set; }
    public string NextName { get; set; } = "";
}

public class AsyncMethodSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    public Task StartAsync(StartAsyncCommand2578 command)
    {
        Id = command.Id;
        Name = command.Name;
        return Task.CompletedTask;
    }

    public Task HandleAsync(HandleAsyncCommand2578 command)
    {
        Name = command.NextName;
        return Task.CompletedTask;
    }
}

public class OrchestrateAsyncCommand2578
{
    public Guid SagaId { get; set; }
}

public class AsyncOrchestrateSaga : Saga
{
    public Guid Id { get; set; }

    // Need a Start so the saga can come into existence for the test, even though
    // we only assert OrchestrateAsync discovery-level behavior here.
    public void Start(InitOrchestrateAsyncSaga command) => Id = command.Id;

    public Task OrchestrateAsync(OrchestrateAsyncCommand2578 command)
    {
        return Task.CompletedTask;
    }
}

public record InitOrchestrateAsyncSaga(Guid Id);

public class ConsumeAsyncCommand2578
{
    public Guid SagaId { get; set; }
}

public class AsyncConsumeSaga : Saga
{
    public Guid Id { get; set; }

    public void Start(InitConsumeAsyncSaga command) => Id = command.Id;

    public Task ConsumeAsync(ConsumeAsyncCommand2578 command)
    {
        return Task.CompletedTask;
    }
}

public record InitConsumeAsyncSaga(Guid Id);

public record StartOrHandleAsyncCommand2578(Guid Id);

public class AsyncStartOrHandleSaga : Saga
{
    public Guid Id { get; set; }

    public Task StartOrHandleAsync(StartOrHandleAsyncCommand2578 command)
    {
        Id = command.Id;
        return Task.CompletedTask;
    }
}

public record NotFoundAsyncCommand2578(Guid Id);

public class AsyncNotFoundSaga : Saga
{
    public Guid Id { get; set; }

    public void Handle(NotFoundAsyncCommand2578 command)
    {
        // Existing-saga path
    }

    public static Task NotFoundAsync(NotFoundAsyncCommand2578 command)
    {
        return Task.CompletedTask;
    }
}

#endregion
