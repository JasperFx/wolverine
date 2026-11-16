using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5196 against real NATS servers: server A is the shared connection, server B the tenant's own. Under the 7.0
/// default, a send to an unknown tenant reaches neither server, a send to a registered tenant reaches only that
/// tenant's server, and a message received for an unknown tenant is dead lettered
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class strict_tenancy_5196 : IAsyncLifetime
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;
    private NatsContainer _serverB = null!;
    private string _serverBUrl = null!;

    public strict_tenancy_5196(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _serverB = new NatsBuilder().WithImage(NatsContainerFixture.NatsImage).Build();
        await _serverB.StartAsync();
        _serverBUrl = _serverB.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        await _serverB.DisposeAsync();
    }

    private Task<IHost> buildSenderAsync(string baseSubject)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancySender";
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureMultiTenancy()
                    .AddTenant("tenantB", cfg => cfg.ConnectionString = _serverBUrl);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(baseSubject).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unknown_tenant_throws_without_a_message_store_and_reaches_neither_server()
    {
        var baseSubject = $"strict.{Guid.NewGuid():N}";

        // Anything under the base subject, with or without a tenant prefix, on either server
        await using var onA = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, ">");
        await using var onB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, ">");

        using var host = await buildSenderAsync(baseSubject);

        var ex = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await host.MessageBus().SendAsync(new OrderPlaced("unknown"), new DeliveryOptions { TenantId = "tenantZ" }));
        ex.TenantId.ShouldBe("tenantZ");

        (await readFor(onA, baseSubject)).ShouldBeNull();
        (await readFor(onB, baseSubject)).ShouldBeNull();
    }

    [Fact]
    public async Task a_known_tenant_reaches_only_its_own_server()
    {
        var baseSubject = $"strict.{Guid.NewGuid():N}";

        await using var onA = await NatsTestHelpers.SubscribeRawAsync(_fixture.ConnectionString, ">");
        await using var onB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, $"tenantB.{baseSubject}");

        using var host = await buildSenderAsync(baseSubject);

        await host.MessageBus().SendAsync(new OrderPlaced("known"), new DeliveryOptions { TenantId = "tenantB" });

        (await onB.ReadAsync(15.Seconds())).ShouldNotBeNull();
        (await readFor(onA, baseSubject)).ShouldBeNull();
    }

    // Reads the subscription until a message under baseSubject shows up, ignoring unrelated traffic on the
    // shared server, or returns null when none does in time
    private static async Task<NatsMsg<byte[]>?> readFor(RawSubscription subscription, string baseSubject)
    {
        var deadline = DateTimeOffset.UtcNow.Add(2.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var message = await subscription.ReadAsync(deadline - DateTimeOffset.UtcNow);
            if (message == null) return null;
            if (message.Value.Subject.Contains(baseSubject)) return message;
        }

        return null;
    }

    // With the tenant-id header trusted, a producer can name a tenant nobody registered. (Untrusted, the 7.0
    // default, the tenant comes from the subject prefix, and a prefix no tenant registered arrives untenanted.)
    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_in_a_trusted_header_is_dead_lettered()
    {
        var subject = $"strictreceive.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancyReceiver";
                opts.UseNats(c =>
                    {
                        c.ConnectionString = _fixture.ConnectionString;
                        c.TrustTenantIdHeader = true;
                    })
                    .AddTenant("a")
                    .AddTenant("b");

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await using var producer = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });

        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .WaitForCondition(new DeadLetteredCondition())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                await producer.PublishAsync(subject,
                    Encoding.UTF8.GetBytes($"{{\"Id\":\"{Guid.NewGuid():N}\"}}"),
                    headers: new NatsHeaders
                    {
                        ["message-type"] = typeof(TenantHeaderProbe).ToMessageTypeName(),
                        ["tenant-id"] = "zzz"
                    });
            }));

        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        record.Exception.ShouldBeOfType<UnknownBrokerTenantException>().TenantId.ShouldBe("zzz");
        session.Executed.MessagesOf<TenantHeaderProbe>().ShouldBeEmpty();
    }

    private class DeadLetteredCondition : ITrackedCondition
    {
        private bool _done;

        public void Record(EnvelopeRecord record)
        {
            if (record.MessageEventType == MessageEventType.MovedToErrorQueue) _done = true;
        }

        public bool IsCompleted() => _done;
    }
}
