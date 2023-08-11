using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class using_native_scheduling
{
    [Fact]
    public async Task with_inline_endpoint()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("inline1").ProcessInline();
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("inline1");
            }).StartAsync();

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(20.Seconds())
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new AsbMessage1("later"), 3.Seconds()));
        
        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe("later");

        await host.StopAsync();
    }
    
    [Fact]
    public async Task with_buffered_endpoint() // durable would have similar mechanics
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("buffered1").BufferedInMemory();
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("buffered1");
            }).StartAsync();

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(20.Seconds())
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new AsbMessage1("in a bit"), 3.Seconds()));
        
        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe("in a bit");

        await host.StopAsync();
    }
}