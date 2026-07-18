using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class prefetch_count_end_to_end : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup()

                    // Transport-wide default prefetch
                    .PrefetchCount(10);

                opts.ListenToAzureServiceBusQueue("prefetch1")

                    // Endpoint override of the transport-wide default
                    .PrefetchCount(20)
                    .BufferedInMemory();

                opts.PublishMessage<AsbPrefetchMessage>().ToAzureServiceBusQueue("prefetch1");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public void prefetch_configuration_is_applied_to_the_endpoint()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();

        transport.PrefetchCount.ShouldBe(10);
        transport.Queues["prefetch1"].PrefetchCount.ShouldBe(20);

        // Any endpoint w/o an explicit override inherits the transport-wide default
        transport.Queues["some.other.queue"].PrefetchCount.ShouldBe(10);
    }

    [Fact]
    public async Task send_and_receive_through_a_prefetching_listener()
    {
        Func<IMessageContext, Task> sendMany = async bus =>
        {
            for (var i = 0; i < 25; i++)
            {
                await bus.SendAsync(new AsbPrefetchMessage(i));
            }
        };

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(sendMany);

        session.Received.MessagesOf<AsbPrefetchMessage>()
            .Select(x => x.Number).OrderBy(x => x)
            .ShouldBe(Enumerable.Range(0, 25));
    }
}

public record AsbPrefetchMessage(int Number);

public static class AsbPrefetchMessageHandler
{
    public static void Handle(AsbPrefetchMessage message)
    {
        // nothing
    }
}
