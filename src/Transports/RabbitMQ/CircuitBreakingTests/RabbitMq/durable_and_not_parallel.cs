using IntegrationTests;
using JasperFx.Core;
using Marten;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;

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
}