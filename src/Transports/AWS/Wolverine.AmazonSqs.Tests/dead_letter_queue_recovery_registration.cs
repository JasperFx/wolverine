using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// Infrastructure-free coverage of the <c>EnableDeadLetterQueueRecovery()</c> registration wiring
/// (#3103): the settings holder and the background recovery listener should be registered exactly
/// once, and explicit queue names should be sanitized and accumulated.
/// </summary>
public class dead_letter_queue_recovery_registration
{
    private static (AmazonSqsDeadLetterQueueRecoverySettings settings, int hostedServiceCount) Build(
        Action<AmazonSqsTransportConfiguration> configure)
    {
        var options = new WolverineOptions();
        var transport = options.Transports.GetOrCreate<AmazonSqsTransport>();
        var configuration = new AmazonSqsTransportConfiguration(transport, options);

        configure(configuration);

        var settings = options.Services
            .Where(x => x.ServiceType == typeof(AmazonSqsDeadLetterQueueRecoverySettings))
            .Select(x => x.ImplementationInstance)
            .OfType<AmazonSqsDeadLetterQueueRecoverySettings>()
            .Single();

        var hostedServiceCount = options.Services
            .Count(x => x.ServiceType == typeof(IHostedService)
                        && x.ImplementationType == typeof(SqsDeadLetterQueueListener));

        return (settings, hostedServiceCount);
    }

    [Fact]
    public void no_arg_overload_registers_listener_with_no_explicit_queue_names()
    {
        var (settings, hostedServiceCount) = Build(c => c.EnableDeadLetterQueueRecovery());

        settings.QueueNames.ShouldBeEmpty();
        hostedServiceCount.ShouldBe(1);
    }

    [Fact]
    public void params_overload_sanitizes_and_records_queue_names()
    {
        var (settings, _) = Build(c => c.EnableDeadLetterQueueRecovery("acme.payments.dlq", "orders-dlq"));

        // Periods are normalized to hyphens just like every other SQS name.
        settings.QueueNames.ShouldBe(["acme-payments-dlq", "orders-dlq"]);
    }

    [Fact]
    public void repeated_calls_register_the_listener_only_once()
    {
        var (settings, hostedServiceCount) = Build(c =>
        {
            c.EnableDeadLetterQueueRecovery("first-dlq");
            c.EnableDeadLetterQueueRecovery("second-dlq");
            c.EnableDeadLetterQueueRecovery();
        });

        hostedServiceCount.ShouldBe(1);
        settings.QueueNames.ShouldBe(["first-dlq", "second-dlq"]);
    }
}
