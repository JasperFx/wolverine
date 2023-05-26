using JasperFx.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

namespace CircuitBreakingTests.RabbitMq;

public class buffered_and_parallel : CircuitBreakerIntegrationContext
{
    public buffered_and_parallel(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureListener(WolverineOptions opts)
    {
        // Requeue failed messages.
        opts.Policies.OnException<BadImageFormatException>().Or<DivideByZeroException>()
            .Requeue();

        opts.PublishAllMessages().ToRabbitQueue("circuit4");
        opts.ListenToRabbitQueue("circuit4").CircuitBreaker(cb =>
        {
            cb.MinimumThreshold = 250;
            cb.PauseTime = 10.Seconds();
            cb.TrackingPeriod = 1.Minutes();
            cb.FailurePercentageThreshold = 20;
        }).BufferedInMemory().MaximumParallelMessages(5);
    }
}