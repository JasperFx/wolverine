using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5827. A requeue or native scheduled retry from a subscription sends its copy to the retry queue, and only to
/// the topic when there is no retry queue (system queues disabled). The retry queue was only known to the main
/// transport, so the listener built for each tenant's namespace (broker-per-tenant multi-tenancy) found none and
/// published its copy to the tenant's topic, where every subscription on that topic received it again, even with
/// system queues enabled. No broker needed: the test watches which entity each namespace's client is asked to send to.
/// </summary>
public class tenant_subscription_requeue_uses_retry_queue_5827
{
    private const string TopicName = "topic5827";
    private const string RetryQueueName = "wolverine.retries.service5827";

    private readonly ServiceBusClient _mainClient = buildClient();
    private readonly ServiceBusClient _tenantClient = buildClient();
    private readonly AzureServiceBusTransport _transport = new();

    public tenant_subscription_requeue_uses_retry_queue_5827()
    {
        _transport.UseServiceBusClient(_mainClient);
        _transport.Tenants["tenant1"].Transport.UseServiceBusClient(_tenantClient);
    }

    private static ServiceBusClient buildClient()
    {
        var client = Substitute.For<ServiceBusClient>();
        client.CreateReceiver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ServiceBusReceiverOptions>())
            .Returns(_ => new IdleReceiver());
        return client;
    }

    private static IWolverineRuntime buildRuntime()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Options.Returns(new WolverineOptions { ServiceName = "service5827" });
        return runtime;
    }

    private async Task buildSubscriptionListenerAsync()
    {
        var subscription = _transport.Topics[TopicName].FindOrCreateSubscription("sub5827");
        subscription.Mode = EndpointMode.BufferedInMemory;

        var listener = await _transport.BuildListenerForSubscription(buildRuntime(), Substitute.For<IReceiver>(),
            subscription);

        await listener.DisposeAsync();
    }

    [Fact]
    public async Task a_tenant_subscription_requeues_to_the_retry_queue_in_the_tenant_namespace()
    {
        // What the main transport's system queue setup does when system queues are enabled
        _transport.RetryQueue = _transport.Queues[RetryQueueName];

        await buildSubscriptionListenerAsync();

        // The main namespace was already right
        _mainClient.Received().CreateSender(RetryQueueName);
        _mainClient.DidNotReceive().CreateSender(TopicName);

        // The tenant namespace has the same retry queue: it is a tenant aware queue of the main transport, so it is
        // provisioned and listened to in every tenant namespace too
        _tenantClient.Received().CreateSender(RetryQueueName);
        _tenantClient.DidNotReceive().CreateSender(TopicName);
    }

    [Fact]
    public async Task a_global_retry_queue_takes_a_tenant_subscription_requeue_in_the_main_namespace()
    {
        // A global queue is only listened to in the main namespace
        _transport.RetryQueue = _transport.Queues[RetryQueueName];
        _transport.RetryQueue.TenancyBehavior = TenancyBehavior.Global;

        await buildSubscriptionListenerAsync();

        // One for the main namespace's listener, one for the tenant namespace's
        _mainClient.Received(2).CreateSender(RetryQueueName);
        _mainClient.DidNotReceive().CreateSender(TopicName);

        _tenantClient.DidNotReceive().CreateSender(Arg.Any<string>());
    }

    [Fact]
    public async Task without_a_retry_queue_every_namespace_still_falls_back_to_its_topic()
    {
        // System queues disabled: no retry queue anywhere, which is what GH-5809's warning reports
        _transport.RetryQueue = null;

        await buildSubscriptionListenerAsync();

        _mainClient.Received().CreateSender(TopicName);
        _tenantClient.Received().CreateSender(TopicName);
    }

    // A receiver that has nothing to hand out until the listener is disposed
    private class IdleReceiver : ServiceBusReceiver
    {
        public override async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages,
            TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
