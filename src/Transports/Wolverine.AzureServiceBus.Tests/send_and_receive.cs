using System.Threading.Tasks;
using Baseline.Dates;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class send_and_receive : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("send_and_receive");

                opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new AsbMessage("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);
        
        session.Received.SingleMessage<AsbMessage>()
            .Name.ShouldBe(message.Name);
    }
}

public record AsbMessage(string Name);

public static class AsbMessageHandler
{
    public static void Handle(AsbMessage message)
    {
        // nothing
    }
}