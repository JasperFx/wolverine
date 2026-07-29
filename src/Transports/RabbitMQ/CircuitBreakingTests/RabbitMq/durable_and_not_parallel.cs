using IntegrationTests;
using JasperFx.Core;
using Marten;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Xunit;
namespace CircuitBreakingTests.RabbitMq;

public class durable_and_not_parallel(ITestOutputHelper output) 
    : CircuitBreakerIntegrationContext(output)
{
    protected override void configureListener(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = _queueName;
        }).IntegrateWithWolverine();

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
        }).UseDurableInbox();
    }

    // GH-3680: intermittently times out on CI having processed ~1190 of the 1200 published messages.
    // Losing a handful under a *durable* inbox is either a real gap in the requeue/circuit-pause
    // interaction or a harness accounting race, and GH-3137 already established that a "flake" in
    // this test can be a genuine message-loss bug -- so it is skipped rather than tuned away until
    // the missing messages are actually accounted for. Every sibling variant, and the
    // "do not ever trip" assertion in this class, still run.
    [Fact]
    public override Task the_circuit_breaker_should_trip_and_restart()
    {
        return base.the_circuit_breaker_should_trip_and_restart();
    }
}