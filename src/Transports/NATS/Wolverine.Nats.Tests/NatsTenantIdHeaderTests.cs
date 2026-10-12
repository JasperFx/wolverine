using System.Collections.Concurrent;
using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A tenant-aware listener on the shared connection took the tenant id from the <c>tenant-id</c> header of the
/// message, so any producer that could publish to the subject could act in another tenant's name. With
/// <c>TrustTenantIdHeader = false</c> the tenant comes only from where the message arrived.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsTenantIdHeaderTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsTenantIdHeaderTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Theory]
    [InlineData(false, "a", "a")]
    [InlineData(false, "", "")]
    [InlineData(true, "a", "b")]
    [InlineData(true, "", "b")]
    public async Task the_tenant_comes_from_the_subject_unless_the_header_is_trusted(bool trustHeader, string prefix,
        string expectedTenant)
    {
        var subject = $"tenantheader.{Guid.NewGuid():N}";
        var id = Guid.NewGuid().ToString("N");
        TenantHeaderProbeHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantHeader";
                opts.UseNats(c =>
                    {
                        c.ConnectionString = _fixture.ConnectionString;
                        c.TrustTenantIdHeader = trustHeader;
                    })
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("a")
                    .AddTenant("b");

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // A producer claiming to act for tenant b, on tenant a's subject or on the plain one
        await using var producer = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await producer.PublishAsync(prefix == "" ? subject : $"{prefix}.{subject}",
            Encoding.UTF8.GetBytes($"{{\"Id\":\"{id}\"}}"),
            headers: new NatsHeaders
            {
                ["message-type"] = typeof(TenantHeaderProbe).ToMessageTypeName(),
                ["tenant-id"] = "b"
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        string? tenant = null;
        while (DateTimeOffset.UtcNow < deadline && !TenantHeaderProbeHandler.Received.TryGetValue(id, out tenant))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        TenantHeaderProbeHandler.Received.ContainsKey(id).ShouldBeTrue();
        (tenant ?? "").ShouldBe(expectedTenant);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task a_requeued_core_message_keeps_the_tenant_of_its_subject(bool inline)
    {
        // Core NATS has no redelivery, so a requeue publishes the message again. To the plain subject it lost the
        // tenant prefix it arrived with, and with the header not trusted the tenant with it
        var subject = $"tenantrequeue.{Guid.NewGuid():N}";
        var id = Guid.NewGuid().ToString("N");
        RequeueTenantProbeHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantRequeue";
                opts.UseNats(c =>
                    {
                        c.ConnectionString = _fixture.ConnectionString;
                        c.TrustTenantIdHeader = false;
                    })
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("a");

                opts.OnException<RequeueTenantProbeException>().Requeue();
                opts.Policies.DisableConventionalLocalRouting();

                var listener = opts.ListenToNatsSubject(subject);
                if (inline)
                {
                    listener.ProcessInline();
                }
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await using var producer = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await producer.PublishAsync($"a.{subject}",
            Encoding.UTF8.GetBytes($"{{\"Id\":\"{id}\"}}"),
            headers: new NatsHeaders { ["message-type"] = typeof(RequeueTenantProbe).ToMessageTypeName() },
            cancellationToken: TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        string? tenant = null;
        while (DateTimeOffset.UtcNow < deadline && !RequeueTenantProbeHandler.Received.TryGetValue(id, out tenant))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        RequeueTenantProbeHandler.Attempts[id].ShouldBe(2);
        tenant.ShouldBe("a");
    }
}

public record TenantHeaderProbe(string Id);

public record RequeueTenantProbe(string Id);

public class RequeueTenantProbeException : Exception;

public static class RequeueTenantProbeHandler
{
    public static ConcurrentDictionary<string, int> Attempts { get; private set; } = new();
    public static ConcurrentDictionary<string, string?> Received { get; private set; } = new();

    public static void Reset()
    {
        Attempts = new ConcurrentDictionary<string, int>();
        Received = new ConcurrentDictionary<string, string?>();
    }

    public static void Handle(RequeueTenantProbe message, Envelope envelope)
    {
        if (Attempts.AddOrUpdate(message.Id, 1, (_, attempts) => attempts + 1) == 1)
        {
            throw new RequeueTenantProbeException();
        }

        Received[message.Id] = envelope.TenantId;
    }
}

public static class TenantHeaderProbeHandler
{
    public static ConcurrentDictionary<string, string?> Received { get; private set; } = new();

    public static void Reset() => Received = new ConcurrentDictionary<string, string?>();

    public static void Handle(TenantHeaderProbe message, Envelope envelope)
    {
        Received[message.Id] = envelope.TenantId;
    }
}
