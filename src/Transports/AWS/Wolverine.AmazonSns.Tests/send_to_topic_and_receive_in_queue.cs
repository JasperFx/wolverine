using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSqs;
using Wolverine.Tracking;

namespace Wolverine.AmazonSns.Tests;

public class send_to_topic_and_receive_in_queue : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision().AutoPurgeOnStartup();
                
                opts.ListenToSqsQueue("send_to_topic_and_receive_in_queue").ReceiveSnsTopicMessage();
                
                opts.UseAmazonSnsTransportLocally()
                    .AutoProvision();

                opts.PublishMessage<SnsMessage>()
                    .ToSnsTopic("send_to_topic_and_receive_in_queue")
                    .SubscribeSqsQueue("send_to_topic_and_receive_in_queue");
            }).StartAsync();
    }

    public async Task DisposeAsync()
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
    }

    [Fact]
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
    
    [Fact]
    public async Task send_to_topic_and_receive_in_queue_many_messages()
    {
        Func<IMessageBus, Task> sending = async bus =>
        {
            for (var i = 0; i < 100; i++)
            {
                await bus.PublishAsync(new SnsMessage(Guid.NewGuid().ToString()));
            }
        };

        await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .ExecuteAndWaitAsync(sending);

    }
}

public record SnsMessage(string Name);

public static class SqsMessageHandler
{
    public static void Handle(SnsMessage message)
    {
        // nothing
        var x = 2;
    }
}
