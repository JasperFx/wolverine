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

public class interop_friendly_dead_letter_queue_mechanics: IAsyncLifetime
{
    private readonly string QueueName = Guid.NewGuid().ToString();
    private IHost _host = null!;
    private RabbitMqTransport theTransport = null!;
    private readonly string deadLetterQueueName;

    public interop_friendly_dead_letter_queue_mechanics()
    {
        deadLetterQueueName = QueueName + "_DLQ";
    }

    public async ValueTask InitializeAsync() =>await  ValueTask.CompletedTask;
    private async Task afterBootstrapping()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(QueueName);

                opts.ListenToRabbitQueue(QueueName).DeadLetterQueueing(new DeadLetterQueue(QueueName + "_DLQ", DeadLetterQueueMode.InteropFriendly));

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();

        theTransport = _host
            .Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();
    }

    public async ValueTask DisposeAsync()
    {
        // Try to eliminate queues to keep them from accumulating
        if (_host != null)
        {
            await _host.StopAsync();
            await _host.TeardownResources();
            _host.Dispose();
        }
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

    /// <summary>
    /// GH-4559. "on created queues" means the broker's copy. <c>RabbitMqQueue.Arguments</c> is the
    /// dictionary Wolverine assembles to PASS to <c>QueueDeclareAsync</c>, so asserting it is true whether
    /// or not the declaration ever reached Rabbit -- and a negative assertion over that dictionary is
    /// satisfied by a queue that was never created at all.
    /// </summary>
    [Fact]
    public async Task does_not_set_the_dead_letter_queue_exchange_on_created_queues()
    {
        await afterBootstrapping();

        using var probe = await RabbitManagementProbe.RequireAsync(TestContext.Current.CancellationToken);

        var arguments = await probe.GetQueueArgumentsAsync(QueueName, token: TestContext.Current.CancellationToken);

        arguments.ShouldNotBeNull("Auto provisioning never created the queue, so its arguments prove nothing");
        arguments.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task move_failed_messages_to_the_dlq()
    {
        await afterBootstrapping();

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new AlwaysErrors());

        var initialQueue = theTransport.Queues[QueueName];
        var deadLetterQueue = theTransport.Queues[deadLetterQueueName];

        (await initialQueue.QueuedCountAsync()).ShouldBe(0);

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var queuedCount = await deadLetterQueue.QueuedCountAsync();
            if (queuedCount > 0) return;

            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        throw new Exception("Never got a message in the dead letter queue");
    }
}