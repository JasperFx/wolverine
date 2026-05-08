using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// Coverage for the per-transport <c>DefaultDeadLetterQueueName(...)</c> override
/// added in #2653. Each test asserts a single permutation of the resolution rule:
/// per-listener overrides win → transport-wide default → built-in
/// <c>"wolverine-dead-letter-queue"</c> fallback. <c>DisableAllNativeDeadLetterQueues()</c>
/// is the master kill-switch and trumps every other knob.
///
/// Tests use <c>UseAmazonSqsTransportLocally()</c> + <c>StartAsync</c> rather than
/// inspecting types in isolation so the exact resolution Wolverine performs at host
/// build time — the resolved <see cref="AmazonSqsQueue.DeadLetterQueueName"/> after
/// every listener-config callback has run — is what's exercised. Provisioning-side
/// behaviour is verified by checking which queues the transport's <c>Queues</c>
/// cache contains (mirrors <c>disabling_dead_letter_queue</c>).
/// </summary>
public class default_dead_letter_queue_name
{
    private static AmazonSqsTransport TransportFor(IHost host) =>
        host.Services.GetRequiredService<IWolverineRuntime>()
            .As<WolverineRuntime>()
            .Options.Transports.GetOrCreate<AmazonSqsTransport>();

    [Fact]
    public async Task fallback_default_name_is_wolverine_dead_letter_queue_when_neither_is_configured()
    {
        // No transport-wide override, no per-listener override — historical behaviour
        // is preserved: every queue resolves to the built-in default.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.ListenToSqsQueue("orders");
            }).StartAsync();

        var transport = TransportFor(host);

        transport.DefaultDeadLetterQueueName.ShouldBe(AmazonSqsTransport.DeadLetterQueueName);
        transport.Queues["orders"].DeadLetterQueueName.ShouldBe(AmazonSqsTransport.DeadLetterQueueName);
    }

    [Fact]
    public async Task transport_default_applies_to_listeners_with_no_per_endpoint_override()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .AutoProvision();

                opts.ListenToSqsQueue("orders");
                opts.ListenToSqsQueue("shipments");
            }).StartAsync();

        var transport = TransportFor(host);

        transport.DefaultDeadLetterQueueName.ShouldBe("my-service-dlq");
        transport.Queues["orders"].DeadLetterQueueName.ShouldBe("my-service-dlq");
        transport.Queues["shipments"].DeadLetterQueueName.ShouldBe("my-service-dlq");
    }

    [Fact]
    public async Task per_endpoint_override_wins_over_transport_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .AutoProvision();

                // Per-listener override wins for this listener only.
                opts.ListenToSqsQueue("orders")
                    .ConfigureDeadLetterQueue("orders-errors");

                // No override here — should still pick up the transport default.
                opts.ListenToSqsQueue("shipments");
            }).StartAsync();

        var transport = TransportFor(host);

        transport.Queues["orders"].DeadLetterQueueName.ShouldBe("orders-errors");
        transport.Queues["shipments"].DeadLetterQueueName.ShouldBe("my-service-dlq");
    }

    [Fact]
    public async Task per_endpoint_disable_wins_over_transport_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .AutoProvision();

                opts.ListenToSqsQueue("noisy-listener")
                    .DisableDeadLetterQueueing();

                opts.ListenToSqsQueue("orders");
            }).StartAsync();

        var transport = TransportFor(host);

        // Disabled listener resolves to null even though the transport default is set.
        transport.Queues["noisy-listener"].DeadLetterQueueName.ShouldBeNull();

        // Other listeners still inherit the transport default.
        transport.Queues["orders"].DeadLetterQueueName.ShouldBe("my-service-dlq");
    }

    [Fact]
    public async Task global_disable_trumps_transport_default_during_provisioning()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .DisableAllNativeDeadLetterQueues();

                opts.ListenToSqsQueue("orders");
            }).StartAsync();

        var transport = TransportFor(host);

        // The DefaultDeadLetterQueueName property still records the user's intent —
        // it's the DisableDeadLetterQueues kill-switch that suppresses provisioning
        // and TryBuildDeadLetterSender (matches existing behaviour with the built-in
        // default name).
        transport.DisableDeadLetterQueues.ShouldBeTrue();

        // No DLQ named "my-service-dlq" should be auto-provisioned.
        transport.Queues.Contains("my-service-dlq").ShouldBeFalse();

        // And no DLQ named with the historical default either.
        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName).ShouldBeFalse();
    }

    [Fact]
    public async Task transport_default_is_sanitized_consistently_with_per_listener_configuration()
    {
        // SanitizeSqsName replaces periods with hyphens so an SQS API call that
        // disallows them succeeds. Per-listener ConfigureDeadLetterQueue does the
        // same; the transport-level setter must match so a name authored with dots
        // produces the identical canonical form on every queue.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("acme.payments.dlq")
                    .AutoProvision();

                opts.ListenToSqsQueue("orders");
            }).StartAsync();

        var transport = TransportFor(host);

        transport.DefaultDeadLetterQueueName.ShouldBe("acme-payments-dlq");
        transport.Queues["orders"].DeadLetterQueueName.ShouldBe("acme-payments-dlq");
    }

    [Fact]
    public async Task transport_default_is_provisioned_when_listeners_inherit_it()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .AutoProvision();

                opts.ListenToSqsQueue("orders");
                opts.ListenToSqsQueue("shipments");
            }).StartAsync();

        var transport = TransportFor(host);

        // The custom DLQ is enumerated by AmazonSqsTransport.endpoints() because
        // every inheriting listener resolves DeadLetterQueueName to "my-service-dlq",
        // and the historical default name is no longer referenced by anyone.
        transport.Queues.Contains("my-service-dlq").ShouldBeTrue();
        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName).ShouldBeFalse();
    }

    [Fact]
    public async Task mixed_inherit_and_override_provisions_both_dead_letter_queues()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .AutoProvision();

                opts.ListenToSqsQueue("orders");                       // inherits "my-service-dlq"
                opts.ListenToSqsQueue("payments")
                    .ConfigureDeadLetterQueue("payments-errors");      // overrides

                opts.ListenToSqsQueue("notifications")
                    .DisableDeadLetterQueueing();                       // disabled, no DLQ provisioned for this one
            }).StartAsync();

        var transport = TransportFor(host);

        transport.Queues.Contains("my-service-dlq").ShouldBeTrue();
        transport.Queues.Contains("payments-errors").ShouldBeTrue();

        // The historical default and the disabled-listener never get provisioned as
        // DLQs.
        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName).ShouldBeFalse();
    }

    [Fact]
    public void default_dead_letter_queue_name_throws_for_null_or_whitespace()
    {
        var transport = new AmazonSqsTransport();
        var configuration = new AmazonSqsTransportConfiguration(transport, new WolverineOptions());

        Should.Throw<ArgumentException>(() => configuration.DefaultDeadLetterQueueName(null!));
        Should.Throw<ArgumentException>(() => configuration.DefaultDeadLetterQueueName(string.Empty));
        Should.Throw<ArgumentException>(() => configuration.DefaultDeadLetterQueueName("   "));
    }

    [Fact]
    public async Task system_queues_keep_their_explicit_no_dlq_setting_under_a_transport_default()
    {
        // tryBuildSystemEndpoints sets queue.DeadLetterQueueName = null on the
        // response queue to opt it out of DLQ provisioning. With the new lazy
        // resolver that null setter goes through and is recorded as an explicit
        // override — so even when the transport-wide DefaultDeadLetterQueueName
        // is set, the response queue still resolves to null. Without this
        // guarantee the Wolverine response/control queues would inherit a DLQ on
        // hosts that opt into DefaultDeadLetterQueueName.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .DefaultDeadLetterQueueName("my-service-dlq")
                    .EnableSystemQueues()
                    .AutoProvision();

                opts.ListenToSqsQueue("orders");
            }).StartAsync();

        var transport = TransportFor(host);

        transport.Queues["orders"].DeadLetterQueueName.ShouldBe("my-service-dlq");

        // Every system queue should resolve to null (explicit DLQ opt-out preserved).
        foreach (var systemQueue in transport.SystemQueues)
        {
            systemQueue.DeadLetterQueueName.ShouldBeNull(
                $"System queue '{systemQueue.QueueName}' should not inherit the transport-wide DLQ.");
        }
    }
}
