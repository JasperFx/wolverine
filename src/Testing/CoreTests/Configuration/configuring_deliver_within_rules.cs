using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestMessages;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

public class configuring_deliver_within_rules
{
    [Fact]
    public async Task configure_remote_subscriber()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(port)
                    .DeliverWithin(3.Seconds());
                opts.ListenAtPort(port);
            }).StartAsync();

        var message = new Message1();

        var session = await host.SendMessageAndWaitAsync(message);
        session.Sent.SingleEnvelope<Message1>()
            .DeliverWithin.ShouldBe(3.Seconds());
    }
    
    [Fact]
    public async Task configure_local_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToLocalQueue("volatile")
                    .DeliverWithin(3.Seconds());
 
            }).StartAsync();

        var message = new Message1();

        var session = await host.SendMessageAndWaitAsync(message);
        session.Sent.SingleEnvelope<Message1>()
            .DeliverWithin.ShouldBe(3.Seconds());
    }
}