using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public record StrictTenancyIncoming(Guid Id);

public class StrictTenancyIncomingHandler
{
    public static void Handle(StrictTenancyIncoming message)
    {
    }
}

/// <summary>
/// GH-5196, issue test 10 against a real broker. A message that reaches the DEFAULT virtual host of a tenanted
/// RabbitMQ transport carrying a tenant id no registered tenant matches (a 6.x node, or anything else that can
/// publish there) is dead lettered with an <see cref="UnknownBrokerTenantException" />, not handled as that tenant.
/// FallbackToDefault and RestoreV6Defaults() handle it as 6.x did
/// </summary>
public class receiving_unknown_tenants_5196
{
    private static async Task declareVirtualHost(string name)
    {
        using var handler = new HttpClientHandler { Credentials = new NetworkCredential("guest", "guest") };
        using var client = new HttpClient(handler);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"http://localhost:15672/api/vhosts/{name}"));
    }

    private static async Task<ITrackedSession> sendFromAnUntenantedNodeAsync(string tenantId,
        Action<WolverineOptions>? configureReceiver = null)
    {
        await declareVirtualHost("strict5196");
        var queueName = "strict-receive-" + Guid.NewGuid().ToString("N");

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "receiver";
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery().IncludeType<StrictTenancyIncomingHandler>();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup()
                    .AddTenant("one", "strict5196");

                opts.ListenToRabbitQueue(queueName);

                configureReceiver?.Invoke(opts);
            }).StartAsync(TestContext.Current.CancellationToken);

        // No tenants at all on the sending node, so the tenant id travels as a header to the default virtual host
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "sender";
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<StrictTenancyIncoming>().ToRabbitQueue(queueName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new StrictTenancyIncoming(Guid.NewGuid()),
                new DeliveryOptions { TenantId = tenantId });

        await receiver.StopAsync(TestContext.Current.CancellationToken);
        await sender.StopAsync(TestContext.Current.CancellationToken);

        return session;
    }

    [Fact]
    public async Task an_unknown_tenant_is_dead_lettered_with_the_unknown_tenant_reason()
    {
        var session = await sendFromAnUntenantedNodeAsync("bogus");

        // Dead lettered before it was deserialized, so the record carries the raw envelope
        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        var ex = record.Exception.ShouldBeOfType<UnknownBrokerTenantException>();
        ex.TenantId.ShouldBe("bogus");
        ex.Message.ShouldContain("Unknown tenant id 'bogus'");

        session.Executed.MessagesOf<StrictTenancyIncoming>().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_registered_tenant_id_on_the_default_virtual_host_is_still_handled()
    {
        var session = await sendFromAnUntenantedNodeAsync("one");

        session.Executed.SingleEnvelope<StrictTenancyIncoming>().TenantId.ShouldBe("one");
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }

    [Fact]
    public async Task restore_v6_defaults_handles_an_unknown_tenant_as_6x_did()
    {
        var session = await sendFromAnUntenantedNodeAsync("bogus", opts => opts.RestoreV6Defaults());

        session.Executed.SingleEnvelope<StrictTenancyIncoming>().TenantId.ShouldBe("bogus");
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }

    [Fact]
    public async Task explicit_fallback_to_default_handles_an_unknown_tenant_as_6x_did()
    {
        var session = await sendFromAnUntenantedNodeAsync("bogus",
            opts => opts.UseRabbitMq().TenantIdBehavior(TenantedIdBehavior.FallbackToDefault));

        session.Executed.SingleEnvelope<StrictTenancyIncoming>().TenantId.ShouldBe("bogus");
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }

    [Fact]
    public async Task tenant_id_required_receives_as_6x_did()
    {
        var session = await sendFromAnUntenantedNodeAsync("bogus",
            opts => opts.UseRabbitMq().TenantIdBehavior(TenantedIdBehavior.TenantIdRequired));

        session.Executed.SingleEnvelope<StrictTenancyIncoming>().TenantId.ShouldBe("bogus");
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }
}
