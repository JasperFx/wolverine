using JasperFx.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class buffered_not_parallelized(ITestOutputHelper output)
    : CircuitBreakerIntegrationContext(output)
{
    // GH-3137: buffered mode acks each message to the broker on receipt and holds it in memory, so the
    // few messages in flight when the breaker tears the listener down on a trip are lost (already acked,
    // never persisted). Require the bulk rather than exact delivery — the trip/restart behavior is still
    // asserted. Durable/inline keep the full-1200 requirement from the base class.
    protected override int RequiredProcessedCountOnTrip => 1150;

    protected override void configureListener(WolverineOptions opts)
    {
        // Requeue failed messages.
        opts.Policies.OnException<BadImageFormatException>()
            .Or<DivideByZeroException>()
            .Requeue();

        opts.PublishAllMessages().ToRabbitQueue(_queueName);
        opts.ListenToRabbitQueue(_queueName).CircuitBreaker(cb =>
        {
            cb.MinimumThreshold = 250;
            cb.PauseTime = 10.Seconds();
            cb.TrackingPeriod = 1.Minutes();
            cb.FailurePercentageThreshold = 20;
        }).BufferedInMemory();
    }
}