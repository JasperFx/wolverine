using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

// GH-5748: AutoPurgeOnStartup purges the queues initialized as the host starts, never a queue the host
// first sends to later, such as the reply queue of the node that sent it a request
public class auto_purge_on_startup_5748 : IAsyncLifetime
{
    private IHost _host = null!;
    private readonly List<string> _queueUrls = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToSqsQueue("purge-5748-" + Guid.NewGuid().ToString("N"));
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var url in _queueUrls)
        {
            await client.DeleteQueueAsync(url);
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    private IAmazonSQS client => _host.Services.GetRequiredService<WolverineOptions>().AmazonSqsTransport().Client!;

    private async Task<string> createQueueHoldingAMessageAsync(string queueName, string body)
    {
        var url = (await client.CreateQueueAsync(queueName, TestContext.Current.CancellationToken)).QueueUrl;
        _queueUrls.Add(url);

        await client.SendMessageAsync(url, body, TestContext.Current.CancellationToken);
        return url;
    }

    private async Task<int> countMessagesAsync(string url)
    {
        var attributes = await client.GetQueueAttributesAsync(url, [QueueAttributeName.ApproximateNumberOfMessages],
            TestContext.Current.CancellationToken);
        return attributes.ApproximateNumberOfMessages;
    }

    [Fact]
    public async Task a_queue_first_sent_to_after_startup_is_not_purged()
    {
        var queueName = "purge-5748-later-" + Guid.NewGuid().ToString("N");
        var url = await createQueueHoldingAMessageAsync(queueName, "already waiting");

        await _host.MessageBus().EndpointFor(new Uri("sqs://" + queueName)).SendAsync(new SqsMessage("sent later"));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (await countMessagesAsync(url) < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        // Both the message that was already there and the one Wolverine sent
        (await countMessagesAsync(url)).ShouldBe(2);
    }

    [Fact]
    public async Task a_queue_known_at_startup_is_still_purged_when_the_host_starts()
    {
        var queueName = "purge-5748-startup-" + Guid.NewGuid().ToString("N");
        var url = await createQueueHoldingAMessageAsync(queueName, "left over");

        (await countMessagesAsync(url)).ShouldBe(1);

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<SqsMessage>().ToSqsQueue(queueName);
            }).StartAsync(TestContext.Current.CancellationToken);

        (await countMessagesAsync(url)).ShouldBe(0);

        await sender.StopAsync(TestContext.Current.CancellationToken);
    }
}
