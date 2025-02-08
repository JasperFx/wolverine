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

public class native_dead_letter_queue_mechanics : IDisposable
{
    private readonly string QueueName = Guid.NewGuid().ToString();
    private IHost _host;
    private RabbitMqTransport theTransport;
    private WolverineOptions theOptions = new WolverineOptions();

    public native_dead_letter_queue_mechanics()
    {
        theOptions.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

        theOptions.PublishAllMessages()
            .ToRabbitQueue(QueueName);

        theOptions.ListenToRabbitQueue(QueueName);

        theOptions.LocalRoutingConventionDisabled = true;
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

        theTransport.Exchanges.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeTrue();
        theTransport.Queues.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeTrue();

        var exchange = theTransport.Queues[RabbitMqTransport.DeadLetterQueueName];
        exchange.Bindings().Single().Queue.ShouldBeSameAs(theTransport.Queues[RabbitMqTransport.DeadLetterQueueName]);
    }

    [Fact]
    public async Task sets_the_dead_letter_queue_exchange_on_created_queues()
    {
        await afterBootstrapping();

        var queue = theTransport.Queues[QueueName];

        queue.Arguments[RabbitMqTransport.DeadLetterQueueHeader].ShouldBe(RabbitMqTransport.DeadLetterQueueName);
    }

    [Fact]
    public async Task no_dead_letter_queue_if_disabled()
    {
        var queueName = "queue_with_no_native_dead_letter_queue";
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
                opts.ListenToRabbitQueue(queueName);


            }).StartAsync();


        var transport = host.Services.GetRequiredService<IWolverineRuntime>().Options.RabbitMqTransport();

        transport.Exchanges.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();
        transport.Queues.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();

        transport.Queues[QueueName].Arguments.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task customize_dead_letter_queueing()
    {
        theOptions.UseRabbitMq().CustomizeDeadLetterQueueing(new DeadLetterQueue("dlq"){ExchangeName = "dlq"});

        await afterBootstrapping();

        theTransport.Exchanges.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();
        theTransport.Queues.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();

        theTransport.Exchanges.Contains("dlq").ShouldBeTrue();
        theTransport.Queues.Contains("dlq").ShouldBeTrue();
    }

    [Fact]
    public async Task move_failed_messages_to_the_dlq()
    {
        await afterBootstrapping();

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new AlwaysErrors());

        var initialQueue = theTransport.Queues[QueueName];
        var deadLetterQueue = theTransport.Queues[RabbitMqTransport.DeadLetterQueueName];

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

    [Fact]
    public async Task overriding_dead_letter_queue_for_specific_queue()
    {
        var deadLetterQueueName = QueueName + "_dlq";
        theOptions.ListenToRabbitQueue(QueueName).DeadLetterQueueing(new DeadLetterQueue(deadLetterQueueName));

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

public record AlwaysErrors;

public static class AlwaysErrorsHandler
{
    public static void Handle(AlwaysErrors command)
    {
        throw new DivideByZeroException("Boom.");
    }
}