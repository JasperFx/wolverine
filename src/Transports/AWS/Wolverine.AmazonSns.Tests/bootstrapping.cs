using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;

namespace Wolverine.AmazonSns.Tests;

public class bootstrapping
{
    [Fact]
    public async Task create_an_open_client()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.UseAmazonSnsTransportLocally(); }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSnsTransport();

        // Just a smoke test on configuration here
        var topicNames = await transport.Client.ListTopicsAsync();
    }
    
    [Fact]
    public async Task create_new_topic_on_startup()
    {
        var topicName = "wolverine-" + Guid.NewGuid();
        
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransportLocally();

                opts.PublishMessage<Message1>().ToSnsTopic(topicName);
            }).StartAsync();

         
        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSnsTransport();
        
        var listTopicsResponse = await transport.Client!.ListTopicsAsync();
        listTopicsResponse.ShouldNotBeNull();
        
        var topic = listTopicsResponse.Topics.FirstOrDefault(x => x.TopicArn.Contains(topicName));
        topic.ShouldNotBeNull();
        
        await transport.Client.DeleteTopicAsync(topic.TopicArn);
    }
}
