using IntegrationTests;
using JasperFx.Core;
using Marten;
using JasperFx.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace CircuitBreakingTests;

public class local_durable_queue : CircuitBreakerIntegrationContext
{
    public local_durable_queue(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureListener(WolverineOptions opts)
    {
        opts.PublishAllMessages().ToLocalQueue("durable").UseDurableInbox(new BufferingLimits(5000, 1))
            .CircuitBreaker(cb =>
            {
                cb.MinimumThreshold = 250;
                cb.PauseTime = 10.Seconds();
                cb.TrackingPeriod = 1.Minutes();
                cb.FailurePercentageThreshold = 20;
            })
            ;

        opts.Policies.OnAnyException().Requeue();

        opts.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "circuit_breaker";
        }).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup();

        opts.Services.AddResourceSetupOnStartup();

        opts.Durability.RecoveryBatchSize = 1200;
        opts.Durability.ScheduledJobPollingTime = 1.Seconds();
        opts.Durability.KeepAfterMessageHandling = 1.Days(); // Keeping Handled messages for failure diagnostics
    }
}