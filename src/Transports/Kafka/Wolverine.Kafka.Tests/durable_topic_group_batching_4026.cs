using System.Collections.Concurrent;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-4026. A Durable KafkaTopicGroup (ListenToKafkaTopics) now drains up to MaximumMessagesToReceive
/// already-fetched records into one batched inbox insert, like the single-topic KafkaListener has since
/// GH-3490. End to end over real Kafka + a real Postgres inbox: a burst across both topics is handled
/// exactly once per message and leaves nothing stranded as Incoming.
/// </summary>
public class durable_topic_group_batching_4026 : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;
    private string _alpha = null!;
    private string _beta = null!;

    public async ValueTask InitializeAsync()
    {
        TopicGroupBurstHandler.Reset();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        _alpha = $"group-batch-alpha-{suffix}";
        _beta = $"group-batch-beta-{suffix}";

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c =>
                    {
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                        c.GroupId = $"group-batch-{suffix}";
                    });

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_group_batch");

                opts.ListenToKafkaTopics(_alpha, _beta)
                    .UseDurableInbox()
                    // Small enough that a 60 message burst has to span several drains
                    .MaximumMessagesToReceive(10);

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishMessage<GroupBurstAlpha>().ToKafkaTopic(_alpha).SendInline();
                opts.PublishMessage<GroupBurstBeta>().ToKafkaTopic(_beta).SendInline();

                opts.Services.AddResourceSetupOnStartup();
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
    public async Task a_burst_across_both_topics_is_handled_once_each_and_leaves_nothing_incoming()
    {
        var bus = _sender.MessageBus();
        var ids = new List<Guid>();

        for (var i = 0; i < 30; i++)
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            ids.Add(a);
            ids.Add(b);
            await bus.PublishAsync(new GroupBurstAlpha(a));
            await bus.PublishAsync(new GroupBurstBeta(b));
        }

        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while (TopicGroupBurstHandler.Count < ids.Count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        TopicGroupBurstHandler.Count.ShouldBe(ids.Count);
        foreach (var id in ids)
        {
            TopicGroupBurstHandler.Executions(id).ShouldBe(1);
        }

        // Every record went through the inbox and out again
        var store = _receiver.Services.GetRequiredService<IMessageStore>();
        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(0);
    }
}

public record GroupBurstAlpha(Guid Id);

public record GroupBurstBeta(Guid Id);

public static class TopicGroupBurstHandler
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

    public static void Handle(GroupBurstAlpha message)
    {
        _executions.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
    }

    public static void Handle(GroupBurstBeta message)
    {
        _executions.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
    }
}
