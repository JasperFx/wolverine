using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using RabbitMQ.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

    public async Task afterBootstrapping()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(QueueName);

                opts.ListenToRabbitQueue(QueueName);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();

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
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().CustomizeDeadLetterQueueing(new DeadLetterQueue("dlq"){ExchangeName = "dlq"});;

                opts.PublishAllMessages()
                    .ToRabbitQueue(QueueName);

                opts.ListenToRabbitQueue(QueueName);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();

        theTransport = _host
            .Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();

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
    public async Task uses_overridden_dead_letter_exchange_per_queue_when_transport_has_custom_default()
    {
        var queueName = "queue-alpha";
        var defaultDeadLetterQueueName = "default-dlq";
        var defaultDeadLetterExchangeName = "dlx-exchange";
        var overriddenDeadLetterQueueName = "queue-alpha-dlx";

        var options = new WolverineOptions();
        options.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue(defaultDeadLetterQueueName)
            {
                ExchangeName = defaultDeadLetterExchangeName
            });

        options.ListenToRabbitQueue(queueName, q => q.QueueType = QueueType.quorum)
            .DeadLetterQueueing(new DeadLetterQueue(overriddenDeadLetterQueueName));

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);

        var transport = options.RabbitMqTransport();
        var queue = transport.Queues[transport.MaybeCorrectName(queueName)];

        queue.Compile(runtime);

        var channel = Substitute.For<IChannel>();
        channel.QueueDeclareAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object>>())
            .Returns(Task.FromResult(new QueueDeclareOk(queue.QueueName, 0, 0)));

        await queue.DeclareAsync(channel, NullLogger.Instance);

        queue.Arguments[RabbitMqTransport.DeadLetterQueueHeader]
            .ShouldBe(overriddenDeadLetterQueueName);
    }

    [Fact]
    public void keeps_per_queue_dead_letter_exchange_when_transport_has_custom_default()
    {
        var queueName = "queue-beta";
        var defaultDeadLetterQueueName = "default-dlq";
        var defaultDeadLetterExchangeName = "dlx-exchange";
        var overriddenDeadLetterQueueName = "queue-beta-dlx";
        var overriddenDeadLetterExchangeName = "queue-beta-dlx-exchange";

        var options = new WolverineOptions();
        var transportExpression = options.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue(defaultDeadLetterQueueName)
            {
                ExchangeName = defaultDeadLetterExchangeName
            });
        var transport = transportExpression.Transport;

        options.ListenToRabbitQueue(queueName)
            .DeadLetterQueueing(new DeadLetterQueue(overriddenDeadLetterQueueName)
        {
            ExchangeName = overriddenDeadLetterExchangeName
        });

        var queue = transport.Queues[transport.MaybeCorrectName(queueName)];

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);

        queue.Compile(runtime);

        queue.DeadLetterQueue!.ExchangeName.ShouldBe(overriddenDeadLetterExchangeName);
        transport.DeadLetterQueue.ExchangeName.ShouldBe(defaultDeadLetterExchangeName);
    }

    [Fact]
    public async Task default_and_override_queues_keep_their_own_dlx_exchange_on_declare()
    {
        var defaultExchange = "default-dlx-exchange";
        var defaultQueue = "queue-default";
        var overrideQueue = "queue-override";
        var overrideExchange = "override-dlx-exchange";

        var options = new WolverineOptions();
        var transport = options.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("default-dlq")
            {
                ExchangeName = defaultExchange
            }).Transport;

        options.ListenToRabbitQueue(defaultQueue);
        options.ListenToRabbitQueue(overrideQueue)
            .DeadLetterQueueing(new DeadLetterQueue(overrideQueue + "-dlq")
            {
                ExchangeName = overrideExchange
            });

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);

        var defaultEndpoint = transport.Queues[transport.MaybeCorrectName(defaultQueue)];
        var overrideEndpoint = transport.Queues[transport.MaybeCorrectName(overrideQueue)];

        defaultEndpoint.Compile(runtime);
        overrideEndpoint.Compile(runtime);

        var channel = Substitute.For<IChannel>();
        channel.QueueDeclareAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object>>())
            .Returns(Task.FromResult(new QueueDeclareOk(defaultQueue, 0, 0)));

        await defaultEndpoint.DeclareAsync(channel, NullLogger.Instance);
        await overrideEndpoint.DeclareAsync(channel, NullLogger.Instance);

        defaultEndpoint.Arguments[RabbitMqTransport.DeadLetterQueueHeader].ShouldBe(defaultExchange);
        overrideEndpoint.Arguments[RabbitMqTransport.DeadLetterQueueHeader].ShouldBe(overrideExchange);
    }

    [Fact]
    public async Task overriding_dead_letter_queue_for_specific_queue()
    {
        var deadLetterQueueName = QueueName + "_dlq";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages()
                    .ToRabbitQueue(QueueName + "Different");

                opts.LocalRoutingConventionDisabled = true;

                opts.ListenToRabbitQueue(QueueName + "Different").DeadLetterQueueing(new DeadLetterQueue(deadLetterQueueName));
            }).StartAsync();

        theTransport = _host
            .Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new AlwaysErrors());

        var initialQueue = theTransport.Queues[QueueName + "Different"];
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