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
        var topicNames = await transport.SnsClient.ListTopicsAsync("0");
    }

    [Fact]
    public async Task create_new_topic_on_startup()
    {
        var topicName = "wolverine-" + Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransportLocally().AutoProvision();

                opts.PublishMessage<Message1>().ToSnsTopic(topicName);
            }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSnsTransport();

        // Just a smoke test on configuration here
        var response = await transport.SnsClient.ListTopicsAsync("0");
        var topic = response.Topics.FirstOrDefault(x => x.TopicArn.Contains(topicName));
        topic.ShouldNotBeNull();
        topic.TopicArn.ShouldNotBeNull();

        await transport.SnsClient.DeleteTopicAsync(topic.TopicArn);
    }

    [Fact]
    public async Task auto_purge_topic_on_startup_smoke_test()
    {
        var topicName = "wolverine-" + Guid.NewGuid();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransportLocally().AutoPurgeOnStartup().AutoProvision();

                opts.PublishMessage<Message1>().ToSnsTopic(topicName);
            }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSnsTransport();

        var response = await transport.SnsClient.ListTopicsAsync("0");
        var topic = response.Topics.FirstOrDefault(x => x.TopicArn.Contains(topicName));
        topic.ShouldNotBeNull();
        topic.TopicArn.ShouldNotBeNull();

        await transport.SnsClient.DeleteTopicAsync(topic.TopicArn);
    }
}
