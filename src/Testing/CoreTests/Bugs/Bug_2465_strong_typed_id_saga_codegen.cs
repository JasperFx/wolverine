using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

// Reproduces https://github.com/JasperFx/wolverine/issues/2465
// Using a saga with a strong-typed ID caused CS0103/CS0246 code gen errors
// because PullSagaIdFromEnvelopeFrame used NameInCode() (bare type name) instead
// of FullNameInCode() (fully qualified name) when emitting TryParse calls for
// message types that carry no saga ID field (envelope path).

public class Bug_2465_strong_typed_id_saga_codegen : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StrongIdSaga));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void host_starts_successfully_with_strong_typed_saga_id()
    {
        // The host starting without exception verifies code generation succeeded.
        // StrongIdSagaWork has no saga ID field so PullSagaIdFromEnvelopeFrame
        // is used. Before the fix, it emitted `StrongSagaId` (bare) instead of
        // `CoreTests.Bugs.StrongSagaId` (fully qualified), causing CS0246.
        _host.ShouldNotBeNull();
    }

    [Fact]
    public async Task can_start_saga_with_strong_typed_id()
    {
        var id = StrongSagaId.New();
        await _host.InvokeMessageAndWaitAsync(new StartStrongIdSaga(id));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        var saga = persistor.Load<StrongIdSaga>(id);
        saga.ShouldNotBeNull();
        saga!.Id.ShouldBe(id);
    }

    [Fact]
    public async Task handle_message_via_envelope_saga_id_path()
    {
        // StartStrongIdSaga.Start() cascades a StrongIdSagaWork message.
        // That message has no saga ID field so Wolverine uses PullSagaIdFromEnvelopeFrame
        // to parse the saga ID from envelope.SagaId. This is the code path that was broken.
        var id = StrongSagaId.New();
        await _host.SendMessageAndWaitAsync(new StartStrongIdSaga(id));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        var saga = persistor.Load<StrongIdSaga>(id);
        saga.ShouldNotBeNull();
        saga!.WorkDone.ShouldBeTrue();
    }
}

// Strong-typed ID: a record struct wrapping Guid with TryParse, placed in this
// namespace so FullNameInCode() is required to reference it in generated code.
public record struct StrongSagaId(Guid Value)
{
    public static StrongSagaId New() => new(Guid.NewGuid());

    public static bool TryParse(string? input, out StrongSagaId result)
    {
        if (Guid.TryParse(input, out var guid))
        {
            result = new StrongSagaId(guid);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString();
}

public record StartStrongIdSaga(StrongSagaId Id);

// No saga ID field — forces Wolverine to use PullSagaIdFromEnvelopeFrame
public record StrongIdSagaWork;

public class StrongIdSaga : Saga
{
    public StrongSagaId Id { get; set; }
    public bool WorkDone { get; set; }

    // Returns a cascaded StrongIdSagaWork; Wolverine will propagate SagaId on that envelope
    public static (StrongIdSaga, StrongIdSagaWork) Start(StartStrongIdSaga cmd)
    {
        return (new StrongIdSaga { Id = cmd.Id }, new StrongIdSagaWork());
    }

    // StrongIdSagaWork has no saga ID → uses PullSagaIdFromEnvelopeFrame
    public void Handle(StrongIdSagaWork work)
    {
        WorkDone = true;
    }
}
