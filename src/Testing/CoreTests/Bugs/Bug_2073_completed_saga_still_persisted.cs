using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_2073_completed_saga_still_persisted : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ImmediatelyCompletedSaga));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task completed_saga_from_static_start_should_not_be_persisted()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartAndComplete(id));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        var saga = persistor.Load<ImmediatelyCompletedSaga>(id);

        // The saga was marked as completed in Start, so it should NOT be persisted
        saga.ShouldBeNull();
    }

    [Fact]
    public async Task non_completed_saga_from_static_start_should_be_persisted()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartWithoutComplete(id));

        var persistor = _host.Services.GetRequiredService<InMemorySagaPersistor>();
        var saga = persistor.Load<ImmediatelyCompletedSaga>(id);

        // The saga was NOT marked as completed, so it SHOULD be persisted
        saga.ShouldNotBeNull();
    }
}

public record StartAndComplete(Guid Id);
public record StartWithoutComplete(Guid Id);

public class ImmediatelyCompletedSaga : Saga
{
    public Guid Id { get; set; }

    public static ImmediatelyCompletedSaga Start(StartAndComplete command)
    {
        var saga = new ImmediatelyCompletedSaga { Id = command.Id };
        saga.MarkCompleted();
        return saga;
    }

    public static ImmediatelyCompletedSaga Start(StartWithoutComplete command)
    {
        return new ImmediatelyCompletedSaga { Id = command.Id };
    }
}
