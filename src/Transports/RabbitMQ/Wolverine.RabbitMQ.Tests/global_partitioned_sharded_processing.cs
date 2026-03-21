using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class global_partitioned_sharded_processing
{
    private readonly ITestOutputHelper _output;

    public global_partitioned_sharded_processing(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task pumpOutMessages(IMessageContext bus)
    {
        var tasks = new Task[5];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < 5; j++)
                {
                    var id = Guid.NewGuid();

                    await bus.PublishAsync(new LogA(id));
                    await bus.PublishAsync(new LogB(id));
                    await bus.PublishAsync(new LogC(id));
                    await bus.PublishAsync(new LogD(id));
                    await bus.PublishAsync(new LogD(id));
                    await bus.PublishAsync(new LogC(id));
                    await bus.PublishAsync(new LogB(id));
                    await bus.PublishAsync(new LogA(id));
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task hammer_it_with_lots_of_messages_global_partitioned()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(LetterMessageHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gletters_rabbit";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.MessagePartitioning.ByMessage<ILetterMessage>(x => x.Id.ToString());

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedRabbitQueues("gletters", 4);
                    topology.MessagesImplementing<ILetterMessage>();
                });
            }).StartAsync();

        var tracked = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .ExecuteAndWaitAsync(pumpOutMessages);

        var envelopes = tracked.Executed.Envelopes().ToArray();

        var counts = envelopes.GroupBy(x => x.Destination);
        foreach (var count in counts)
        {
            _output.WriteLine(count.Key?.ToString() + " had " + count.Count());
        }

        // In single-node mode, global partitioning routes directly to companion local queues
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters1/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters2/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters3/")).ShouldBeTrue();
        envelopes.Any(x => x.Destination == new Uri("local://global-gletters4/")).ShouldBeTrue();
    }
}
