using JasperFx.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class inline_not_parallelized(ITestOutputHelper output)
    : CircuitBreakerIntegrationContext(output)
{
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
        }).ProcessInline();
    }
}