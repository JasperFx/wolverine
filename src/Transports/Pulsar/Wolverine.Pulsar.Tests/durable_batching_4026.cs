using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// GH-4026. A Durable Pulsar listener now coalesces consumed messages for up to 5ms (or
/// MaximumMessagesToReceive) into one batched inbox insert, like the RabbitMQ, Kafka and NATS
/// listeners. End to end over a real Pulsar + a real Postgres inbox: a burst is handled exactly once
/// per message and leaves nothing stranded as Incoming.
/// </summary>
[Collection("acceptance")]
public class durable_batching_4026 : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        PulsarBurstHandler.Reset();

        var topicPath = $"persistent://public/default/durable-batch-{Guid.NewGuid():N}";

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "pulsar_batch_4026");

                opts.ListenToPulsarTopic(topicPath)
                    .UseDurableInbox()
                    // Small enough that a 60 message burst has to span several windows
                    .MaximumMessagesToReceive(10);

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishMessage<PulsarBurst>().ToPulsarTopic(topicPath).SendInline();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task a_burst_is_handled_once_each_and_leaves_nothing_incoming()
    {
        var bus = _sender.MessageBus();
        var ids = new List<Guid>();

        for (var i = 0; i < 60; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await bus.PublishAsync(new PulsarBurst(id));
        }

        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while (PulsarBurstHandler.Count < ids.Count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        PulsarBurstHandler.Count.ShouldBe(ids.Count);
        foreach (var id in ids)
        {
            PulsarBurstHandler.Executions(id).ShouldBe(1);
        }

        // Every message went through the inbox and out again
        var store = _receiver.Services.GetRequiredService<IMessageStore>();
        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(0);
    }
}

public record PulsarBurst(Guid Id);

public static class PulsarBurstHandler
{
    private static readonly ConcurrentDictionary<Guid, int> _executions = new();

    public static int Count => _executions.Count;

    public static int Executions(Guid id)
    {
        return _executions.GetValueOrDefault(id);
    }

    public static void Reset()
    {
        _executions.Clear();
    }

    public static void Handle(PulsarBurst message)
    {
        _executions.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
    }
}
