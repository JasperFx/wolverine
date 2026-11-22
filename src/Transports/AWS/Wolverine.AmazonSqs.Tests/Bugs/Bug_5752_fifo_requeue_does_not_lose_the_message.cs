using System.Collections.Concurrent;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;

namespace Wolverine.AmazonSqs.Tests.Bugs;

/// <summary>
/// GH-5752. On a FIFO queue a message handed back with <c>Requeue()</c> was lost: the copy reused the explicit
/// deduplication id, which SQS drops inside its 5 minute window while answering the send with success, and a
/// raw JSON copy carried no <c>MessageGroupId</c>, which SQS rejects. The original had already been deleted.
///
/// <para>The requeued copy still goes to the back of its message group; this pins only that nothing is lost.</para>
/// </summary>
public class Bug_5752_fifo_requeue_does_not_lose_the_message
{
    private const int Count = 5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task every_message_is_handled_after_a_requeue(bool rawJson)
    {
        var queueName = $"gh5752-{(rawJson ? "raw" : "wolverine")}-{Guid.NewGuid().ToString("N")[..8]}.fifo";
        var tracker = new RequeueTracker5752();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                var listener = opts.ListenToSqsQueue(queueName)
                    .ConfigureQueueCreation(request =>
                    {
                        request.Attributes ??= new Dictionary<string, string>();
                        request.Attributes[QueueAttributeName.FifoQueue] = "true";
                    })
                    .ProcessInline();

                if (rawJson)
                {
                    listener.ReceiveRawJsonMessage(typeof(RequeuePing5752));
                }

                opts.PublishMessage<RequeuePing5752>().ToSqsQueue(queueName)
                    .ConfigureQueueCreation(request =>
                    {
                        request.Attributes ??= new Dictionary<string, string>();
                        request.Attributes[QueueAttributeName.FifoQueue] = "true";
                    });
                opts.Policies.OnException<RequeueFailure5752>().Requeue();

                opts.Services.AddSingleton(tracker);
            }).StartAsync(TestContext.Current.CancellationToken);

        if (rawJson)
        {
            var client = new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
                new AmazonSQSConfig { ServiceURL = "http://localhost:4566", AuthenticationRegion = "us-east-1" });
            var url = (await client.GetQueueUrlAsync(queueName, TestContext.Current.CancellationToken)).QueueUrl;
            for (var i = 1; i <= Count; i++)
            {
                await client.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = url,
                    MessageBody = $$"""{"Id":{{i}}}""",
                    MessageGroupId = "group-1",
                    MessageDeduplicationId = $"ping-{i}"
                }, TestContext.Current.CancellationToken);
            }
        }
        else
        {
            var bus = host.MessageBus();
            for (var i = 1; i <= Count; i++)
            {
                await bus.SendAsync(new RequeuePing5752(i),
                    new DeliveryOptions { GroupId = "group-1", DeduplicationId = $"ping-{i}" });
            }
        }

        await tracker.AllHandled.Task.WaitAsync(45.Seconds(), TestContext.Current.CancellationToken);

        tracker.Handled.OrderBy(x => x).ShouldBe([1, 2, 3, 4, 5]);
    }
}

public record RequeuePing5752(int Id);

public class RequeueFailure5752() : Exception("message 1 fails once");

public class RequeueTracker5752
{
    private int _failedOnce;
    public ConcurrentQueue<int> Handled { get; } = new();
    public TaskCompletionSource AllHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Record(RequeuePing5752 ping)
    {
        if (ping.Id == 1 && Interlocked.Exchange(ref _failedOnce, 1) == 0)
        {
            throw new RequeueFailure5752();
        }

        Handled.Enqueue(ping.Id);
        if (Handled.Count == 5) AllHandled.TrySetResult();
    }
}

public static class RequeuePing5752Handler
{
    public static void Handle(RequeuePing5752 ping, RequeueTracker5752 tracker) => tracker.Record(ping);
}
