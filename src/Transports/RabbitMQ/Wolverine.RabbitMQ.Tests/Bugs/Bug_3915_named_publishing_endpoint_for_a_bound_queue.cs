using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

// GH-3915: .Named() is documented as a logical label for logging and metrics, and RabbitMqQueue only
// seeds EndpointName from the queue name -- so the two are equal until somebody renames the endpoint.
// RabbitMqBinding bound by EndpointName, which meant naming a publishing endpoint for an
// already-bound queue asked Rabbit to bind a queue that had never been declared:
//
//   NOT_FOUND - no queue 'main-queue-publisher' in vhost '/'
//
// ...and the application failed to start.
public class Bug_3915_named_publishing_endpoint_for_a_bound_queue : IAsyncLifetime
{
    private const string ExchangeName = "bug3915-exchange";
    private const string QueueName = "bug3915-queue";
    private const string RoutingKey = "bug3915-key";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var rabbit = opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                rabbit.BindExchange(ExchangeName, ex =>
                {
                    ex.ExchangeType = ExchangeType.Direct;
                    ex.IsDurable = true;
                    ex.AutoDelete = false;
                }).ToQueue(QueueName, RoutingKey, q =>
                {
                    q.IsDurable = true;
                    q.AutoDelete = false;
                });

                opts.ListenToRabbitQueue(QueueName).DisableDeadLetterQueueing();

                // The endpoint name differs from the queue name. Before GH-3915 the binding was declared
                // against this label instead of the queue, and StartAsync() threw.
                opts.PublishAllMessages()
                    .ToRabbitQueue(QueueName)
                    .Named("bug3915-publisher");

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Bug3915MessageHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void the_binding_is_declared_against_the_queue_not_the_endpoint_name()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        var queue = transport.Queues[QueueName];

        // The rename took effect...
        transport.Endpoints().OfType<RabbitMqQueue>()
            .Any(x => x.EndpointName == "bug3915-publisher")
            .ShouldBeTrue();

        // ...and the binding still names the physical queue
        var binding = queue.Bindings().Single(x => x.ExchangeName == ExchangeName);
        binding.Queue.QueueName.ShouldBe(QueueName);
        binding.HasDeclared.ShouldBeTrue();
    }

    [Fact]
    public async Task can_still_send_and_receive_through_the_renamed_endpoint()
    {
        var session = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new Bug3915Message("hello"));

        session.Received.SingleMessage<Bug3915Message>()
            .Name.ShouldBe("hello");
    }
}

public record Bug3915Message(string Name);

public static class Bug3915MessageHandler
{
    public static void Handle(Bug3915Message message)
    {
    }
}
