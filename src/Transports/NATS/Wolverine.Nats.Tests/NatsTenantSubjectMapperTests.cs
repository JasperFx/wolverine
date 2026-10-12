using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A tenant on its own connection -- its own server or account -- does not need a subject prefix to be told apart,
/// and its producers usually publish to the plain subject. The per-tenant subject mapper of
/// <c>AddTenant(id, mapper, cfg)</c> reached only the sending side, though: the tenant's listener still subscribed
/// with the transport's mapper, so a JetStream listener filtered on <c>*.subject</c> and silently never took a message
/// published to <c>subject</c>.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsTenantSubjectMapperTests : IClassFixture<SecondNatsServer>
{
    private readonly NatsContainerFixture _fixture;
    private readonly SecondNatsServer _tenantServer;
    private readonly ITestOutputHelper _output;

    public NatsTenantSubjectMapperTests(NatsContainerFixture fixture, SecondNatsServer tenantServer,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _tenantServer = tenantServer;
        _output = output;
    }

    [Fact]
    public async Task a_tenant_listener_subscribes_with_the_tenants_own_mapper()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var stream = $"TENANTMAPPER_{id}";
        var subject = $"tenantmapper.{id}.in";
        TenantMapperProbeHandler.Reset();

        // The shared listener needs the stream on the transport's server, the tenant listener on the tenant's
        var js = await _tenantServer.JetStreamAsync();
        await js.CreateStreamAsync(new StreamConfig(stream, [$"tenantmapper.{id}.>"]),
            TestContext.Current.CancellationToken);
        await using (var shared = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString }))
        {
            await shared.CreateJetStreamContext().CreateStreamAsync(
                new StreamConfig(stream, [$"tenantmapper.{id}.>"]),
                TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantMapper";
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("t1", new SameSubjectTenantMapper(),
                        cfg => cfg.ConnectionString = _tenantServer.ConnectionString);

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject).UseJetStream(stream, $"tenantmapper-{id}");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // A producer on the tenant's own server, publishing to the plain subject
        await js.PublishAsync(subject, System.Text.Encoding.UTF8.GetBytes($"{{\"Id\":\"{id}\"}}"),
            headers: new NatsHeaders { ["message-type"] = typeof(TenantMapperProbe).ToMessageTypeName() },
            cancellationToken: TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline && TenantMapperProbeHandler.Received.IsEmpty)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        TenantMapperProbeHandler.Received.ShouldContain($"{id}@t1");
    }

    [Fact]
    public async Task a_send_to_the_tenant_goes_to_the_plain_subject_on_its_server()
    {
        var subject = $"tenantmapperout.{Guid.NewGuid():N}";
        await using var onTenantServer = await NatsTestHelpers.SubscribeRawAsync(_tenantServer.ConnectionString, subject);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantMapperOut";
                opts.UseNats(_fixture.ConnectionString)
                    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
                    .AddTenant("t1", new SameSubjectTenantMapper(),
                        cfg => cfg.ConnectionString = _tenantServer.ConnectionString);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().PublishAsync(new OrderPlaced("down"), new DeliveryOptions { TenantId = "t1" });

        (await onTenantServer.ReadAsync(15.Seconds())).ShouldNotBeNull();
    }
}

public record TenantMapperProbe(string Id);

public static class TenantMapperProbeHandler
{
    public static ConcurrentBag<string> Received { get; private set; } = new();

    public static void Reset() => Received = new ConcurrentBag<string>();

    public static void Handle(TenantMapperProbe message, Envelope envelope)
    {
        Received.Add($"{message.Id}@{envelope.TenantId}");
    }
}
