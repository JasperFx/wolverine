using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5196. In a real host, the senders a tenanted RabbitMQ transport builds take the tenancy behavior resolved when
/// the transport started: dead letter unknown tenants in 7.0, and fall back to the default broker after
/// RestoreV6Defaults(), even when RestoreV6Defaults() is called after UseRabbitMq()
/// </summary>
public class strict_tenancy_defaults_5196
{
    private static async Task<TenantedIdBehavior> senderBehaviorAsync(Action<WolverineOptions> configure)
    {
        var queueName = "strict-tenancy-" + Guid.NewGuid().ToString("N");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing()
                    .AddTenant("one", "/");

                opts.PublishMessage<MultiTenantMessage>().ToRabbitQueue(queueName);

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var sender = transport.Queues[queueName].ResolveSender(runtime).ShouldBeOfType<TenantedSender>();

        var behavior = sender.TenantedIdBehavior;
        await host.StopAsync(TestContext.Current.CancellationToken);
        return behavior;
    }

    [Fact]
    public async Task the_7_0_default_dead_letters_unknown_tenants()
    {
        (await senderBehaviorAsync(_ => { })).ShouldBe(TenantedIdBehavior.DeadLetterUnknownTenants);
    }

    [Fact]
    public async Task restore_v6_defaults_after_use_rabbit_mq_falls_back_to_the_default_broker()
    {
        (await senderBehaviorAsync(opts => opts.RestoreV6Defaults())).ShouldBe(TenantedIdBehavior.FallbackToDefault);
    }
}
