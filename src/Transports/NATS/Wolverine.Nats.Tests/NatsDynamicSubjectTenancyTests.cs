using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Regression coverage for the intersection of the two features this branch adds: per-message dynamic subjects
/// (<c>RoutingMode.ByTopic</c> / <c>Envelope.TopicName</c>) and subject-isolation multi-tenancy.
///
/// A static endpoint subject is tenant-qualified once, at sender construction (<c>NatsEndpoint.CreateSender</c>).
/// A subject computed per message is not — so without the fix a subject-isolation tenant's dynamic-subject send
/// would publish to the raw, un-prefixed subject on the shared connection, silently defeating isolation. This
/// asserts the computed subject is tenant-qualified (<c>{tenantId}.{computed}</c>) and does not leak to the other
/// tenant's subject or the bare (un-prefixed) subject.
///
/// Uses raw NATS subscribers on the exact expected subjects (a single shared broker), so it is a local sanity
/// check and is <b>not</b> required to run in CI.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDynamicSubjectTenancyTests
{
    private readonly ITestOutputHelper _output;

    public NatsDynamicSubjectTenancyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task dynamic_subject_is_tenant_qualified_for_subject_isolation_tenants()
    {
        var url = NatsTestHelpers.ResolveUrl();
        if (!await NatsTestHelpers.IsNatsAvailable(url))
        {
            return;
        }

        var root = $"orders.events.{Guid.NewGuid():N}";
        var orderId = Guid.NewGuid().ToString("N");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DynamicSubjectTenancy";
                opts.UseNats(url)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    // Both tenants share the connection; isolation is purely by subject prefix.
                    .AddTenant("tenantA")
                    .AddTenant("tenantB");

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessagesToNatsSubject<OrderPlaced>(m => $"{root}.{m.OrderId}").SendInline();
            })
            .StartAsync();

        // Each tenant's dynamic subject must be tenant-qualified: {tenantId}.{computed subject}.
        await using var subA = await NatsTestHelpers.SubscribeRawAsync(url, $"tenantA.{root}.{orderId}");
        await using var subB = await NatsTestHelpers.SubscribeRawAsync(url, $"tenantB.{root}.{orderId}");
        // The un-prefixed subject proves nothing escapes isolation (this is where the pre-fix bug landed).
        await using var subBare = await NatsTestHelpers.SubscribeRawAsync(url, $"{root}.{orderId}");

        await host.MessageBus().SendAsync(new OrderPlaced(orderId), new DeliveryOptions { TenantId = "tenantA" });

        var onA = await subA.ReadAsync(15.Seconds());
        onA.ShouldNotBeNull();
        onA!.Value.Subject.ShouldBe($"tenantA.{root}.{orderId}");

        // Not on the other tenant's subject, and not on the bare un-prefixed subject.
        (await subB.ReadAsync(2.Seconds())).ShouldBeNull();
        (await subBare.ReadAsync(2.Seconds())).ShouldBeNull();
    }
}
