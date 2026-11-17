using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// GH-5586. With a message store, a full durable local queue leaves what is published to it in the inbox for
/// recovery. Without one there is nothing to recover it from, so the queue has to keep taking everything, and it must
/// still never make the publisher wait for room.
/// </summary>
public class full_durable_local_queue_without_a_message_store_5586 : IAsyncLifetime
{
    private const int FillCount = DurableLocalQueue.InMemoryLimit + 1;

    private readonly NoStoreGate theGate = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(full_durable_local_queue_without_a_message_store_5586).Assembly;
            opts.Services.AddSingleton<IMessageStore>(new NullMessageStore());
            opts.Services.AddSingleton(theGate);
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoStoreGateMessageHandler));

            opts.PublishMessage<NoStoreGateMessage>().ToLocalQueue("no-store-full").UseDurableInbox().Sequential();
        }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        theGate.Release();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_publish_past_the_limit_neither_waits_nor_loses_the_message()
    {
        var bus = _host.MessageBus();
        for (var i = 0; i < FillCount; i++)
        {
            await bus.PublishAsync(new NoStoreGateMessage(Guid.NewGuid()));
        }

        await theGate.Started.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        var queue = (DurableLocalQueue)_host.GetRuntime().Endpoints
            .GetOrBuildSendingAgent(new Uri("local://no-store-full"));
        queue.IsFull.ShouldBeFalse("Nothing could recover a message left in a store that does not exist");

        var publishing = bus.PublishAsync(new NoStoreGateMessage(Guid.NewGuid())).AsTask();
        var finished = await Task.WhenAny(publishing, Task.Delay(10.Seconds(), TestContext.Current.CancellationToken));
        finished.ShouldBe(publishing, "The publish is waiting for room in the full local queue");
        await publishing;

        theGate.Release();
        await theGate.WaitForAsync(FillCount + 1, 1.Minutes());
    }
}

public record NoStoreGateMessage(Guid Id);

public class NoStoreGate
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, int> _handled = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public void Release() => _gate.TrySetResult();

    public async Task PassAsync(NoStoreGateMessage message)
    {
        _started.TrySetResult();
        await _gate.Task;
        _handled.AddOrUpdate(message.Id, 1, (_, count) => count + 1);
    }

    public async Task WaitForAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (_handled.Count < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        _handled.Count.ShouldBe(expected);
        _handled.Values.ShouldAllBe(count => count == 1);
    }
}

[WolverineIgnore]
public static class NoStoreGateMessageHandler
{
    public static Task Handle(NoStoreGateMessage message, NoStoreGate gate) => gate.PassAsync(message);
}
