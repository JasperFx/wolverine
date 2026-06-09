using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class RabbitMqQueueTests
{
    private readonly IChannel theChannel = Substitute.For<IChannel>();

    private readonly RabbitMqTransport theTransport = new();

    [Fact]
    public void defaults()
    {
        var queue = new RabbitMqQueue("foo", new RabbitMqTransport());

        queue.EndpointName.ShouldBe("foo");
        queue.IsDurable.ShouldBeTrue();
        queue.IsExclusive.ShouldBeFalse();
        queue.AutoDelete.ShouldBeFalse();
        queue.Arguments.Any().ShouldBeFalse();
        
        queue.QueueType.ShouldBe(QueueType.classic);
    }

    [Fact]
    public void uri_construction_from_different_broker()
    {
        var transport = new RabbitMqTransport("random");
        var queue = new RabbitMqQueue("foo", transport);
        queue.Uri.ShouldBe(new Uri("random://queue/foo"));

    }

    [Fact]
    public async Task publish_queue_dead_letter_queueing_sets_a_specific_dlq()
    {
        var options = new WolverineOptions();
        options.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("default-dlx")
            {
                ExchangeName = "default-dlx-exchange"
            });

        var config = options.PublishMessage<PublishOverrideMessage>()
            .ToRabbitQueue("publish-queue");

        config.DeadLetterQueueing(new DeadLetterQueue("publish-dlx")
        {
            ExchangeName = "publish-dlx-exchange"
        });

        ((IDelayedEndpointConfiguration)config).Apply();

        var transport = options.RabbitMqTransport();
        var queue = transport.Queues[transport.MaybeCorrectName("publish-queue")];

        queue.DeadLetterQueue!.QueueName.ShouldBe("publish-dlx");
        queue.DeadLetterQueue.ExchangeName.ShouldBe("publish-dlx-exchange");

        var channel = Substitute.For<IChannel>();
        channel.QueueDeclareAsync(default!, default, default, default, default!)
            .ReturnsForAnyArgs(Task.FromResult(new QueueDeclareOk("publish-queue", 0, 0)));

        await queue.DeclareAsync(channel, NullLogger.Instance);

        await channel.Received().QueueDeclareAsync(
            "publish-queue",
            queue.IsDurable,
            queue.IsExclusive,
            queue.AutoDelete,
            Arg.Is<IDictionary<string, object?>>(args =>
                args.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader) &&
                Equals(args[RabbitMqTransport.DeadLetterQueueHeader], "publish-dlx-exchange")));
    }

    [Fact]
    public async Task publish_queue_disable_dead_letter_queueing_clears_the_dlq()
    {
        var options = new WolverineOptions();
        options.UseRabbitMq()
            .CustomizeDeadLetterQueueing(new DeadLetterQueue("default-dlx")
            {
                ExchangeName = "default-dlx-exchange"
            });

        var config = options.PublishMessage<PublishOverrideMessage>()
            .ToRabbitQueue("publish-queue");

        config.DisableDeadLetterQueueing();
        ((IDelayedEndpointConfiguration)config).Apply();

        var transport = options.RabbitMqTransport();
        var queue = transport.Queues[transport.MaybeCorrectName("publish-queue")];

        queue.DeadLetterQueue.ShouldBeNull();

        var channel = Substitute.For<IChannel>();
        channel.QueueDeclareAsync(default!, default, default, default, default!)
            .ReturnsForAnyArgs(Task.FromResult(new QueueDeclareOk("publish-queue", 0, 0)));

        await queue.DeclareAsync(channel, NullLogger.Instance);

        await channel.Received().QueueDeclareAsync(
            "publish-queue",
            queue.IsDurable,
            queue.IsExclusive,
            queue.AutoDelete,
            Arg.Is<IDictionary<string, object?>>(args =>
                !args.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader)));
    }

    [Fact]
    public void set_time_to_live()
    {
        var queue = new RabbitMqQueue("foo", new RabbitMqTransport());
        queue.TimeToLive(3.Minutes());
        queue.Arguments["x-message-ttl"].ShouldBe(180000);
    }

    [Fact]
    public void uri_construction()
    {
        var queue = new RabbitMqQueue("foo", new RabbitMqTransport());
        queue.Uri.ShouldBe(new Uri("rabbitmq://queue/foo"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task declare(bool autoDelete, bool isExclusive, bool isDurable)
    {
        var queue = new RabbitMqQueue("foo", new RabbitMqTransport())
        {
            AutoDelete = autoDelete,
            IsExclusive = isExclusive,
            IsDurable = isDurable
        };

        queue.HasDeclared.ShouldBeFalse();

        var channel = Substitute.For<IChannel>();
        await queue.DeclareAsync(channel, NullLogger.Instance);

        await channel.Received()
            .QueueDeclareAsync("foo", queue.IsDurable, queue.IsExclusive, queue.AutoDelete, (IDictionary<string, object?>)queue.Arguments);

        queue.HasDeclared.ShouldBeTrue();
    }

    [Fact]
    public async Task initialize_with_no_auto_provision_or_auto_purge()
    {
        theTransport.AutoProvision = false;
        theTransport.AutoPurgeAllQueues = false;
        var queue = new RabbitMqQueue("foo", theTransport);

        theTransport.Queues["foo"].PurgeOnStartup = false;

        await queue.InitializeAsync(theChannel, NullLogger.Instance);

        await theChannel.DidNotReceiveWithAnyArgs().QueueDeclareAsync("foo", true, true, true, null);
        await theChannel.DidNotReceiveWithAnyArgs().QueuePurgeAsync("foo");
    }

    public record PublishOverrideMessage;

    [Fact]
    public async Task initialize_with_no_auto_provision_but_auto_purge_on_endpoint_only()
    {
        theTransport.AutoProvision = false;
        theTransport.AutoPurgeAllQueues = false;

        var endpoint = theTransport.Queues["foo"];
        endpoint.PurgeOnStartup = true;

        await endpoint.InitializeAsync(theChannel, NullLogger.Instance);

        await theChannel.DidNotReceiveWithAnyArgs().QueueDeclareAsync("foo", true, true, true, null);
        await theChannel.Received().QueuePurgeAsync("foo");
    }

    [Fact]
    public async Task initialize_with_no_auto_provision_but_global_auto_purge()
    {
        theTransport.AutoProvision = false;
        theTransport.AutoPurgeAllQueues = true;

        var endpoint = new RabbitMqQueue("foo", theTransport);

        theTransport.Queues["foo"].PurgeOnStartup = false;

        await endpoint.InitializeAsync(theChannel, NullLogger.Instance);

        await theChannel.DidNotReceiveWithAnyArgs().QueueDeclareAsync("foo", true, true, true, null);
        await theChannel.Received().QueuePurgeAsync("foo");
    }

    [Fact]
    public async Task initialize_with_auto_provision_and_global_auto_purge()
    {
        theTransport.AutoProvision = true;
        theTransport.AutoPurgeAllQueues = true;

        var endpoint = new RabbitMqQueue("foo", theTransport);

        theTransport.Queues["foo"].PurgeOnStartup = false;

        await endpoint.InitializeAsync(theChannel, NullLogger.Instance);

        await theChannel.Received().QueueDeclareAsync("foo", true, false, false, (IDictionary<string, object?>)endpoint.Arguments);
        await theChannel.Received().QueuePurgeAsync("foo");
    }

    [Fact]
    public async Task initialize_with_auto_provision_and_local_auto_purge()
    {
        theTransport.AutoProvision = true;
        theTransport.AutoPurgeAllQueues = false;

        var endpoint = theTransport.Queues["foo"];
        endpoint.PurgeOnStartup = true;

        await endpoint.InitializeAsync(theChannel, NullLogger.Instance);

        await theChannel.Received().QueueDeclareAsync("foo", true, false, false, (IDictionary<string, object?>)endpoint.Arguments);
        await theChannel.Received().QueuePurgeAsync("foo");
    }
}
