using System.Diagnostics;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// GH-2521: [SagaIdentityFrom] attribute is ignored when NotFound handler is declared
/// before Handle in the saga class. The code generation was only scanning the first
/// handler method found (via DistinctBy), which would be NotFound if declared first.
/// </summary>
public class Bug_2521_saga_identity_from_ordering : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SagaWithNotFoundDeclaredFirst));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task saga_identity_from_attribute_works_when_notfound_is_declared_first()
    {
        var sagaId = Guid.NewGuid().ToString();

        // Start the saga
        await _host.InvokeMessageAndWaitAsync(new StartNotFoundFirstSaga(sagaId));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        persistor.Load<SagaWithNotFoundDeclaredFirst>(sagaId).ShouldNotBeNull();

        // Reset tracking
        SagaWithNotFoundDeclaredFirst.HandleCalled = false;
        SagaWithNotFoundDeclaredFirst.NotFoundCalled = false;

        // Send message with non-conventional property name — should find saga via [SagaIdentityFrom]
        await _host.InvokeMessageAndWaitAsync(new NotFoundFirstMsg(sagaId));

        // Handle should have been called on the existing saga, not NotFound
        SagaWithNotFoundDeclaredFirst.HandleCalled.ShouldBeTrue(
            "Handle() should be called when saga exists and [SagaIdentityFrom] correctly resolves the saga ID");
        SagaWithNotFoundDeclaredFirst.NotFoundCalled.ShouldBeFalse(
            "NotFound() should NOT be called when saga exists");
    }

    [Fact]
    public async Task notfound_is_called_when_saga_does_not_exist()
    {
        SagaWithNotFoundDeclaredFirst.HandleCalled = false;
        SagaWithNotFoundDeclaredFirst.NotFoundCalled = false;

        // Send message for non-existent saga
        await _host.InvokeMessageAndWaitAsync(new NotFoundFirstMsg(Guid.NewGuid().ToString()));

        SagaWithNotFoundDeclaredFirst.NotFoundCalled.ShouldBeTrue();
        SagaWithNotFoundDeclaredFirst.HandleCalled.ShouldBeFalse();
    }
}

public record StartNotFoundFirstSaga(string Id);

// Message with non-conventional property name (not "SagaWithNotFoundDeclaredFirstId", "SagaId", or "Id")
public record NotFoundFirstMsg(string TargetId);

// Key: NotFound is declared BEFORE Handle — this is what triggers GH-2521
public class SagaWithNotFoundDeclaredFirst : Saga
{
    public string Id { get; set; } = string.Empty;

    public static bool HandleCalled { get; set; }
    public static bool NotFoundCalled { get; set; }

    public static SagaWithNotFoundDeclaredFirst Start(StartNotFoundFirstSaga cmd)
        => new() { Id = cmd.Id };

    // NotFound declared FIRST — before Handle
    public static void NotFound(NotFoundFirstMsg msg)
    {
        NotFoundCalled = true;
        Debug.WriteLine($"NotFound called for TargetId {msg.TargetId}");
    }

    // Handle declared SECOND — with [SagaIdentityFrom] on a non-conventional property
    public void Handle(
        [SagaIdentityFrom(nameof(NotFoundFirstMsg.TargetId))] NotFoundFirstMsg msg)
    {
        HandleCalled = true;
        Debug.WriteLine($"Handle called for saga {Id}");
    }
}
