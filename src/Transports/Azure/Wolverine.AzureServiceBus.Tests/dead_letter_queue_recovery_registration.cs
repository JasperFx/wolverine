using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// Infrastructure-free coverage of the Azure Service Bus <c>EnableDeadLetterQueueRecovery()</c>
/// registration wiring (#3103): the settings holder and the background recovery listener should be
/// registered exactly once, and explicit queue/subscription names should be accumulated.
/// </summary>
public class dead_letter_queue_recovery_registration
{
    private static (AzureServiceBusDeadLetterQueueRecoverySettings settings, int hostedServiceCount) Build(
        Action<AzureServiceBusConfiguration> configure)
    {
        var options = new WolverineOptions();
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var configuration = new AzureServiceBusConfiguration(transport, options);

        configure(configuration);

        var settings = options.Services
            .Where(x => x.ServiceType == typeof(AzureServiceBusDeadLetterQueueRecoverySettings))
            .Select(x => x.ImplementationInstance)
            .OfType<AzureServiceBusDeadLetterQueueRecoverySettings>()
            .Single();

        var hostedServiceCount = options.Services
            .Count(x => x.ServiceType == typeof(IHostedService)
                        && x.ImplementationType == typeof(AzureServiceBusDeadLetterQueueListener));

        return (settings, hostedServiceCount);
    }

    [Fact]
    public void no_arg_overload_registers_listener_with_no_explicit_names()
    {
        var (settings, hostedServiceCount) = Build(c => c.EnableDeadLetterQueueRecovery());

        settings.EndpointNames.ShouldBeEmpty();
        hostedServiceCount.ShouldBe(1);
    }

    [Fact]
    public void params_overload_records_endpoint_names()
    {
        var (settings, _) = Build(c => c.EnableDeadLetterQueueRecovery("orders", "shipments"));

        settings.EndpointNames.ShouldBe(["orders", "shipments"]);
    }

    [Fact]
    public void repeated_calls_register_the_listener_only_once()
    {
        var (settings, hostedServiceCount) = Build(c =>
        {
            c.EnableDeadLetterQueueRecovery("orders");
            c.EnableDeadLetterQueueRecovery("shipments");
            c.EnableDeadLetterQueueRecovery();
        });

        hostedServiceCount.ShouldBe(1);
        settings.EndpointNames.ShouldBe(["orders", "shipments"]);
    }
}
