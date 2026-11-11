using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-5331. SagaChain.DetermineFrames clears Handlers once it has sorted the saga calls, so a
/// saga message used to drop out of ServiceCapabilities.Sagas the first time it was handled,
/// and the whole saga disappeared once every message had been handled
/// </summary>
public class saga_capabilities_after_the_chains_compile : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CompiledOrderSaga>();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<SagaDescriptor?> readSaga()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);
        return capabilities.Sagas.SingleOrDefault(x => x.StateType.FullName == typeof(CompiledOrderSaga).FullName);
    }

    private static void shouldDescribeEveryMessage(SagaDescriptor? saga)
    {
        saga.ShouldNotBeNull();

        saga.Messages
            .Select(x => (x.MessageType.FullName, x.Role))
            .OrderBy(x => x.FullName)
            .ShouldBe([
                (typeof(CloseCompiledOrder).FullName!, SagaRole.Orchestrate),
                (typeof(ShipCompiledOrder).FullName!, SagaRole.Orchestrate),
                (typeof(StartCompiledOrder).FullName!, SagaRole.Start)
            ]);
    }

    [Fact]
    public async Task every_message_is_still_described_after_each_one_is_handled()
    {
        shouldDescribeEveryMessage(await readSaga());

        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StartCompiledOrder(id));
        shouldDescribeEveryMessage(await readSaga());

        await _host.InvokeMessageAndWaitAsync(new ShipCompiledOrder(id));
        shouldDescribeEveryMessage(await readSaga());

        await _host.InvokeMessageAndWaitAsync(new CloseCompiledOrder(id));
        shouldDescribeEveryMessage(await readSaga());
    }
}

public record StartCompiledOrder(Guid Id);

public record ShipCompiledOrder(Guid Id);

public record CloseCompiledOrder(Guid Id);

public class CompiledOrderSaga : Saga
{
    public Guid Id { get; set; }

    public static CompiledOrderSaga Start(StartCompiledOrder message) => new() { Id = message.Id };

    public void Handle(ShipCompiledOrder message)
    {
    }

    public void Handle(CloseCompiledOrder message) => MarkCompleted();
}
