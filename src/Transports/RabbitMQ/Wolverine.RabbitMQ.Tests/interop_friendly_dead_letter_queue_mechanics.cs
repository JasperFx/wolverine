using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class interop_friendly_dead_letter_queue_mechanics: IDisposable
{
    private readonly string QueueName = Guid.NewGuid().ToString();
    private IHost _host;
    private RabbitMqTransport theTransport;
    private WolverineOptions theOptions = new WolverineOptions();
    private readonly string deadLetterQueueName;

    public interop_friendly_dead_letter_queue_mechanics()
    {
        theOptions.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

        theOptions.PublishAllMessages()
            .ToRabbitQueue(QueueName);

        theOptions.ListenToRabbitQueue(QueueName).DeadLetterQueueing(new DeadLetterQueue(QueueName + "_DLQ", DeadLetterQueueMode.InteropFriendly));

        theOptions.LocalRoutingConventionDisabled = true;

        deadLetterQueueName = QueueName + "_DLQ";
    }

        public async Task afterBootstrapping()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(theOptions).StartAsync();

        theTransport = _host
            .Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();
    }

    public void Dispose()
    {
        // Try to eliminate queues to keep them from accumulating
        _host.TeardownResources();

        _host?.Dispose();
    }

    [Fact]
    public async Task should_have_the_dead_letter_objects_by_default()
    {
        await afterBootstrapping();


        theTransport.Exchanges.Contains(deadLetterQueueName).ShouldBeTrue();
        theTransport.Queues.Contains(deadLetterQueueName).ShouldBeTrue();

        var exchange = theTransport.Queues[deadLetterQueueName];
        exchange.Bindings().Single().Queue.ShouldBeSameAs(theTransport.Queues[deadLetterQueueName]);
    }

    [Fact]
    public async Task does_not_set_the_dead_letter_queue_exchange_on_created_queues()
    {
        await afterBootstrapping();

        var queue = theTransport.Queues[QueueName];

        queue.Arguments.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader).ShouldBeFalse();

    }

    [Fact]
    public async Task move_failed_messages_to_the_dlq()
    {
        await afterBootstrapping();

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new AlwaysErrors());

        var initialQueue = theTransport.Queues[QueueName];
        var deadLetterQueue = theTransport.Queues[deadLetterQueueName];

        (await initialQueue.QueuedCountAsync()).ShouldBe(0);

        var attempts = 0;
        while (attempts < 5)
        {
            var queuedCount = await deadLetterQueue.QueuedCountAsync();
            if (queuedCount > 0) return;

            attempts++;
            await Task.Delay(250.Milliseconds());
        }

        throw new Exception("Never got a message in the dead letter queue");
    }
}