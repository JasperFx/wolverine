using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class end_to_end_with_CloudEvents : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("cloudevents").InteropWithCloudEvents();
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("cloudevents").InteropWithCloudEvents();

                opts.ListenToAzureServiceBusQueue("fifo1")

                    // Require session identifiers with this queue
                    .RequireSessions()

                    // This controls the Wolverine handling to force it to process
                    // messages sequentially
                    .Sequential()
                    .InteropWithCloudEvents();

                opts.PublishMessage<AsbMessage2>()
                    .ToAzureServiceBusQueue("cloudeventsfifo1").InteropWithCloudEvents();

                opts.PublishMessage<AsbMessage3>().ToAzureServiceBusTopic("cloudeventsasb3").InteropWithCloudEvents();
                opts.ListenToAzureServiceBusSubscription("cloudeventsasb3")
                    .FromTopic("asb3")

                    // Require sessions on this subscription
                    .RequireSessions(1)

                    .ProcessInline()
                    .InteropWithCloudEvents();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new AsbMessage1("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe(message.Name);
    }
}