using Baseline.Dates;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class inline_and_parallel : CircuitBreakerIntegrationContext
{
    public inline_and_parallel(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureListener(WolverineOptions opts)
    {
        // Requeue failed messages.
        opts.Handlers.OnException<BadImageFormatException>().Or<DivideByZeroException>()
            .Requeue();

        opts.PublishAllMessages().ToRabbitQueue("circuit3");
        opts.ListenToRabbitQueue("circuit3").CircuitBreaker(cb =>
        {
            cb.MinimumThreshold = 250;
            cb.PauseTime = 10.Seconds();
            cb.TrackingPeriod = 1.Minutes();
            cb.FailurePercentageThreshold = 20;
        }).ProcessInline().MaximumParallelMessages(5);
    }
}
