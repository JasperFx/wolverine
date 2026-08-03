using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs;
using Wolverine.Tracking;

namespace Wolverine.AmazonSns.Tests;

public class send_to_topic_and_receive_in_queue_in_aws : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport()
                    .AutoProvision()
                    .AutoPurgeOnStartup();
                
                opts.ListenToSqsQueue("send_to_topic_and_receive_in_queue").ReceiveSnsTopicMessage();
                
                opts.UseAmazonSnsTransport()
                    .AutoProvision();

                opts.PublishMessage<SnsMessage>()
                    .ToSnsTopic("send_to_topic_and_receive_in_queue")
                    .SubscribeSqsQueue("send_to_topic_and_receive_in_queue");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var options = _host.Services.GetRequiredService<WolverineOptions>();
        
        var sqsTransport = options.AmazonSqsTransport();
        foreach (var queue in sqsTransport.Queues)
        {
            await queue.TeardownAsync(NullLogger.Instance);
        }
        
        var snsTransport = options.AmazonSnsTransport();
        foreach (var topic in snsTransport.Topics)
        {
            await topic.TeardownAsync(NullLogger.Instance);
        }
        
        await _host.StopAsync();
        _host.Dispose();
    }

    // Line-for-line the same test as send_to_topic_and_receive_in_queue, except that it points at a
    // real AWS account instead of LocalStack. It is here to be run by hand when SNS fidelity is in
    // question; CI has no credentials, so it can only ever fail there. Skipped rather than tagged
    // Flaky (#3763) because there is nothing unstable about it.
    [Fact(Skip = "Requires real AWS credentials; the LocalStack twin is send_to_topic_and_receive_in_queue")]
    public async Task send_to_topic_and_receive_in_queue_a_single_message()
    {
        var message = new SnsMessage("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<SnsMessage>()
            .Name.ShouldBe(message.Name);
    }
}
