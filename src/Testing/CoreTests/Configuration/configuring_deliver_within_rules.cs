using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
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
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var message = new Message1();

        var session = await host.TrackActivity().SendMessageAndWaitAsync(message);

        var records = session.AllRecordsInOrder().ToArray();
        
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

            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var message = new Message1();

        var session = await host.SendMessageAndWaitAsync(message);
        // GH-5136. Message1 has several handler types in this assembly, each on its own local queue, so the
        // message sent to "volatile" is relayed from there to each of them. The rule is about the one sent to
        // "volatile"
        session.Sent.Envelopes().Single(x => x.Message is Message1 && x.Destination == new Uri("local://volatile"))
            .DeliverWithin.ShouldBe(3.Seconds());
    }
}