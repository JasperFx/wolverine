using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Tests.Internal;

/// <summary>
/// GH-5737. The SQS listener polled again 250ms after every empty receive, so a very quiet queue still cost ~4,300
/// receive requests a day even with long polling at 20 seconds. IdleDelay() on the listener configuration makes
/// that wait configurable, and the listener has to honor it.
/// </summary>
public class sqs_listener_idle_delay_5737 : IAsyncDisposable
{
    private const string TheQueueUrl = "http://localhost:4566/000000000000/gh5737";

    private readonly IAmazonSQS theClient = Substitute.For<IAmazonSQS>();
    private readonly AmazonSqsTransport theTransport = new();
    private SqsListener? theListener;

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }
    }

    [Fact]
    public void configure_the_idle_delay_on_the_listener()
    {
        var queue = theTransport.Queues["mail"];

        var configuration = new AmazonSqsListenerConfiguration(queue).IdleDelay(5.Minutes());
        ((IDelayedEndpointConfiguration)configuration).Apply();

        queue.IdleDelay.ShouldBe(5.Minutes());
    }

    [Fact]
    public void the_idle_delay_is_unset_by_default()
    {
        theTransport.Queues["mail"].IdleDelay.ShouldBeNull();
    }

    [Fact]
    public async Task a_quiet_queue_is_polled_every_250ms_by_default()
    {
        var queue = await buildQueueAsync();
        theListener = buildListener(queue);

        await Task.Delay(1500.Milliseconds(), TestContext.Current.CancellationToken);

        receiveCount().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task the_listener_waits_the_configured_idle_delay_after_an_empty_receive()
    {
        var queue = await buildQueueAsync();
        queue.IdleDelay = 1.Hours();
        theListener = buildListener(queue);

        await Task.Delay(1500.Milliseconds(), TestContext.Current.CancellationToken);

        // The first poll comes back empty, and the next one is an hour away
        receiveCount().ShouldBe(1);
    }

    [Fact]
    public async Task a_long_idle_delay_does_not_hold_up_shutting_the_listener_down()
    {
        var queue = await buildQueueAsync();
        queue.IdleDelay = 1.Hours();
        theListener = buildListener(queue);

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);

        await theListener.StopAsync().AsTask().WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_tenant_queue_inherits_the_idle_delay()
    {
        var queue = await buildQueueAsync();
        queue.IdleDelay = 5.Minutes();

        var tenant = new AmazonSqsTenant("tenant1");

        queue.BuildTenantSibling(tenant).IdleDelay.ShouldBe(5.Minutes());
    }

    private int receiveCount()
    {
        return theClient.ReceivedCalls()
            .Count(x => x.GetMethodInfo().Name == nameof(IAmazonSQS.ReceiveMessageAsync));
    }

    private async Task<AmazonSqsQueue> buildQueueAsync()
    {
        theTransport.Client = theClient;

        theClient.CreateQueueAsync(Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateQueueResponse { QueueUrl = TheQueueUrl });

        // Every receive comes back empty, the way a quiet queue does once its long poll runs out
        theClient.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiveMessageResponse { Messages = [] });

        var queue = theTransport.Queues["gh5737"];
        await queue.SetupAsync(theClient);
        return queue;
    }

    private SqsListener buildListener(AmazonSqsQueue queue)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());

        return new SqsListener(runtime, queue, theTransport, Substitute.For<IReceiver>());
    }
}
