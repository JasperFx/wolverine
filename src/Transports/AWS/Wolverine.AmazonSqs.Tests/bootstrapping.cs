using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.AmazonSqs.Tests;

public class bootstrapping
{
    [Fact]
    public async Task create_an_open_client()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.UseAmazonSqsTransportLocally(); }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        // Just a smoke test on configuration here
        var queueNames = await transport.SqsClient.ListQueuesAsync("wolverine");
        var topicNames = await transport.SnsClient.ListTopicsAsync();
    }

    [Fact]
    public async Task create_new_queue_on_startup()
    {
        var queueName = "wolverine-" + Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                opts.ListenToSqsQueue("wolverine-" + queueName);
            }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        // Just a smoke test on configuration here
        var queueNames = await transport.SqsClient.ListQueuesAsync("wolverine");
        var queueUrl = queueNames.QueueUrls.FirstOrDefault(x => x.Contains(queueName));
        queueUrl.ShouldNotBeNull();

        await transport.SqsClient.DeleteQueueAsync(queueUrl);
    }

    [Fact]
    public async Task auto_purge_queue_on_startup_smoke_test()
    {
        var queueName = "wolverine-" + Guid.NewGuid();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoPurgeOnStartup().AutoProvision();

                opts.ListenToSqsQueue("wolverine-" + queueName);
            }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        var queueNames = await transport.SqsClient.ListQueuesAsync("wolverine");
        var queueUrl = queueNames.QueueUrls.FirstOrDefault(x => x.Contains(queueName));
        queueUrl.ShouldNotBeNull();

        await transport.SqsClient.DeleteQueueAsync(queueUrl);
    }

    [Fact]
    public async Task configure_listening()
    {
        var queueName = "wolverine-" + Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoPurgeOnStartup().AutoProvision();

                opts.ListenToSqsQueue(queueName, e =>
                {
                    e.VisibilityTimeout = 4;
                    e.MaxNumberOfMessages = 5;
                    e.WaitTimeSeconds = 6;
                });
            }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();

        var endpoint = options.Transports.GetOrCreateEndpoint(new Uri($"{AmazonSqsTransport.SqsProtocol}://{AmazonSqsTransport.SqsSegment}/{queueName}"))
            .ShouldBeOfType<AmazonSqsQueue>();

        endpoint.VisibilityTimeout.ShouldBe(4);
        endpoint.MaxNumberOfMessages.ShouldBe(5);
        endpoint.WaitTimeSeconds.ShouldBe(6);
    }
    
    [Fact]
    public async Task create_new_topic_on_startup()
    {
        var topicName = "wolverine-" + Guid.NewGuid();
        
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally();

                opts.PublishMessage<Message1>().ToSnsTopic(topicName);
            }).StartAsync();

         
        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();
        
        var listTopicsResponse = await transport.SnsClient!.ListTopicsAsync();
        listTopicsResponse.ShouldNotBeNull();
        
        var topic = listTopicsResponse.Topics.FirstOrDefault(x => x.TopicArn.Contains(topicName));
        topic.ShouldNotBeNull();
        
        await transport.SnsClient.DeleteTopicAsync(topic.TopicArn);
    }
}
