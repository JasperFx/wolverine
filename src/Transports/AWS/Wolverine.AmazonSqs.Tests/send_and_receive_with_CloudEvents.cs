using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests;

public class send_and_receive_with_CloudEvents : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // A queue of its own per test rather than one shared queue purged on startup: an SQS purge is not
        // instantaneous, and LocalStack could delete the message a test sent right after purging, so the
        // tracked session timed out waiting for a receive that never came
        var queueName = "cloudevents-" + Guid.NewGuid().ToString("N");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision();

                opts.ListenToSqsQueue(queueName).InteropWithCloudEvents();

                opts.PublishAllMessages().ToSqsQueue(queueName).InteropWithCloudEvents().MessageBatchMaxDegreeOfParallelism(5);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new SqsMessage("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<SqsMessage>()
            .Name.ShouldBe(message.Name);
    }

    [Fact]
    public async Task custom_headers_survive_as_extension_attributes()
    {
        // GH-5080: SQS carries only the CloudEvents document, so the headers must be inside it
        #region sample_sending_custom_headers_with_cloudevents
        // Only lower case header names are carried as CloudEvents extension attributes
        var options = new DeliveryOptions();
        options.Headers["x-user"] = "alice";
        options.Headers["x-country"] = "ca";

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new SqsMessage("Josh Allen"), options);

        // The headers come back out of the CloudEvents document on the receiving side
        var envelope = session.Received.SingleEnvelope<SqsMessage>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
        #endregion
    }

}