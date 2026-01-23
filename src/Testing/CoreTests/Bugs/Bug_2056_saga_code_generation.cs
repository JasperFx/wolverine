using System.Diagnostics;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Bugs;

public class Bug_2056_saga_code_generation : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BugReproSaga))
                    .IncludeType(typeof(AnotherHandler));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task saga_handle_should_be_called_when_saga_exists()
    {
        var id = Guid.NewGuid();

        // Start the saga
        await _host.InvokeMessageAndWaitAsync(new StartSaga(id));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        persistor.Load<BugReproSaga>(id).ShouldNotBeNull();

        // Reset static tracking
        BugReproSaga.HandleCalled = false;
        BugReproSaga.NotFoundCalled = false;

        // Send event that should be handled by existing saga
        await _host.InvokeMessageAndWaitAsync(new SharedEvent { ReferenceId = id });

        // BUG: Handle() is never called because generated code creates new saga
        // instead of loading by ReferenceId
        BugReproSaga.HandleCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task saga_notfound_should_be_called_when_saga_does_not_exist()
    {
        // Reset static tracking
        BugReproSaga.HandleCalled = false;
        BugReproSaga.NotFoundCalled = false;

        // Send event without existing saga - should trigger NotFound
        await _host.InvokeMessageAndWaitAsync(new SharedEvent { ReferenceId = Guid.NewGuid() });

        // BUG: NotFound() is never called
        BugReproSaga.NotFoundCalled.ShouldBeTrue();
    }
}

public record StartSaga(Guid Id);

public record SagaCompleted(Guid Id);

// Shared event between Saga/handlers
public record SharedEvent
{
    public Guid ReferenceId { get; init; }
}

public class BugReproSaga : Saga
{
    public Guid Id { get; set; }

    // Static fields to track which methods were called
    public static bool HandleCalled { get; set; }
    public static bool NotFoundCalled { get; set; }

    public static BugReproSaga Start(StartSaga cmd) => new BugReproSaga { Id = cmd.Id };

    // Instance handler with [SagaIdentityFrom] and cascading return value
    public SagaCompleted Handle(
        [SagaIdentityFrom(nameof(SharedEvent.ReferenceId))] SharedEvent evt)
    {
        HandleCalled = true;
        Debug.WriteLine($"Handle called for saga {Id}");
        return new SagaCompleted(Id);
    }

    // NotFound handler for the same message type
    public static void NotFound(SharedEvent evt)
    {
        NotFoundCalled = true;
        Debug.WriteLine($"NotFound called for ReferenceId {evt.ReferenceId}");
    }
}


public class AnotherHandler
{
    public static void Handle(SharedEvent message)
    {
        Debug.WriteLine($"Handle called for AnotherHandler");
    }
}