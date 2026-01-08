using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class disable_requeueing
{
    [Fact]
    public async Task can_disable()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("").ConsumeOnly();
            }).StartAsync();
        
        host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>()
            .Usage.ShouldBe(KafkaUsage.ConsumeOnly);
    }
}