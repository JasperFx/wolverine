using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-5196 against real Redis servers: server A is the shared connection, server B the tenant's own. Under the 7.0
/// default (ConfigureMultiTenancy() with no argument), a send to an unknown tenant reaches neither server, a send to
/// a registered tenant reaches only that tenant's server, and a message received with an unknown tenant id is
/// dead lettered
/// </summary>
public class strict_tenancy_5196 : IClassFixture<SecondRedisServerFixture>
{
    private readonly string _serverAConn = RedisContainerFixture.ConnectionString;
    private readonly string _serverBConn;

    public strict_tenancy_5196(SecondRedisServerFixture serverB)
    {
        // Fail rather than pass quietly when the second server cannot start
        _serverBConn = serverB.ConnectionString ?? throw new InvalidOperationException(
            "The second Redis server for the broker-per-tenant tests did not start");
    }

    private Task<IHost> buildSenderAsync(string streamKey)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancySender";
                opts.UseRedisTransport(_serverAConn)
                    .AutoProvision()
                    .ConfigureMultiTenancy()
                    .AddTenant("tenantB", _serverBConn);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unknown_tenant_throws_without_a_message_store_and_reaches_neither_server()
    {
        var streamKey = $"strict-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(streamKey);

        var ex = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await host.MessageBus().SendAsync(new RedisBrokerMessage("unknown"),
                new DeliveryOptions { TenantId = "tenantZ" }));
        ex.TenantId.ShouldBe("tenantZ");

        // Give a wrongly sent message time to show up before asserting it never did
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        (await RedisMultiBrokerHelpers.ReadStreamAsync(_serverAConn, streamKey)).ShouldBeEmpty();
        (await RedisMultiBrokerHelpers.ReadStreamAsync(_serverBConn, streamKey)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_known_tenant_reaches_only_its_own_server()
    {
        var streamKey = $"strict-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(streamKey);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("known"), new DeliveryOptions { TenantId = "tenantB" });

        (await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverBConn, streamKey, 15.Seconds())).Length.ShouldBe(1);
        (await RedisMultiBrokerHelpers.ReadStreamAsync(_serverAConn, streamKey)).ShouldBeEmpty();
    }

    [Fact]
    public async Task no_tenant_id_reaches_the_shared_server()
    {
        var streamKey = $"strict-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(streamKey);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("none"));

        (await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverAConn, streamKey, 15.Seconds())).Length.ShouldBe(1);
        (await RedisMultiBrokerHelpers.ReadStreamAsync(_serverBConn, streamKey)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_is_dead_lettered()
    {
        var streamKey = $"strict-{Guid.NewGuid():N}";

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancyReceiver";
                opts.UseRedisTransport(_serverAConn)
                    .AutoProvision()
                    .ConfigureMultiTenancy()
                    .AddTenant("tenantB", _serverBConn);

                opts.ListenToRedisStream(streamKey, "strict-group");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // A node with no tenants at all puts the tenant id on the shared server as a header
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UntenantedSender";
                opts.UseRedisTransport(_serverAConn).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new RedisBrokerMessage("unknown"), new DeliveryOptions { TenantId = "tenantZ" });

        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        record.Exception.ShouldBeOfType<UnknownBrokerTenantException>().TenantId.ShouldBe("tenantZ");
        session.Executed.MessagesOf<RedisBrokerMessage>().ShouldBeEmpty();
    }
}
