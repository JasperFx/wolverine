using System.Text;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

// Dekaf's consumer recycles the pooled buffers behind Header values once the consumer advances, so
// the envelope mappers must copy every header value out eagerly (GetValueAsString/GetValueAsArray)
// while mapping. This regression pushes a stream of distinctly-headered records through a listener
// and asserts no envelope observes another record's (or garbage) header bytes.
public class pooled_header_safety
{
    [Fact]
    public async Task header_values_survive_the_consumer_advancing_past_the_record()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "pooled-headers";
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<PooledHeaderMessage>().ToKafkaTopic("pooled-headers").SendInline();
                opts.ListenToKafkaTopic("pooled-headers").BeginAtEarliest();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        const int count = 100;

        var session = await host.TrackActivity().IncludeExternalTransports().Timeout(90.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async bus =>
            {
                for (var i = 0; i < count; i++)
                {
                    await bus.PublishAsync(new PooledHeaderMessage(i),
                        new DeliveryOptions().WithHeader("expected-index", i.ToString()));
                }
            }));

        var envelopes = session.Received.Envelopes()
            .Where(x => x.Message is PooledHeaderMessage)
            .ToArray();
        envelopes.Length.ShouldBe(count);

        foreach (var envelope in envelopes)
        {
            var message = (PooledHeaderMessage)envelope.Message!;
            envelope.Headers["expected-index"].ShouldBe(message.Index.ToString(),
                $"header for message {message.Index} was corrupted after the consumer advanced");
        }
    }
}

public record PooledHeaderMessage(int Index);

public static class PooledHeaderMessageHandler
{
    public static void Handle(PooledHeaderMessage message)
    {
    }
}
