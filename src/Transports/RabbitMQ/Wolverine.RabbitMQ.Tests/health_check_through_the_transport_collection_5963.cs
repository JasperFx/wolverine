using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5963. Asked through <see cref="ITransport"/>, as Wolverine.HealthChecks and monitoring tools ask it, the RabbitMQ
/// transport answered null for its health check, so a paused listener's broker queue depth was never read.
/// <see cref="broker_queue_depth_5946"/> passed all along because it calls the concrete type.
/// </summary>
public class health_check_through_the_transport_collection_5963
{
    [Fact]
    public async Task the_rabbitmq_health_check_is_found_through_the_transport_collection()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            opts.PublishMessage<DepthMessage>().ToRabbitQueue(queueName).SendInline();
            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        var bus = host.MessageBus();
        for (var i = 0; i < 3; i++)
        {
            await bus.SendAsync(new DepthMessage(i));
        }

        var runtime = host.GetRuntime();
        var healthChecks = runtime.Options.Transports
            .Select(x => x.BuildHealthCheck(runtime))
            .OfType<WolverineTransportHealthCheck>()
            .ToList();

        var healthCheck = healthChecks.OfType<RabbitMqHealthCheck>().ShouldHaveSingleItem();

        var result = await healthCheck.CheckHealthAsync(TestContext.Current.CancellationToken);
        result.Status.ShouldBe(TransportHealthStatus.Healthy);

        // Nothing listens to the queue, so whatever is sent stays there
        var uri = new Uri($"rabbitmq://queue/{queueName}");
        long? depth = null;
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline && depth != 3)
        {
            depth = await healthCheck.GetBrokerQueueDepthAsync(uri, TestContext.Current.CancellationToken);
            if (depth != 3) await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        depth.ShouldBe(3);
    }
}
