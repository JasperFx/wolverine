using System.Data.SqlClient;
using System.Net.Sockets;
using Baseline;
using Baseline.Dates;
using Wolverine;
using Wolverine.ErrorHandling;
using Microsoft.Extensions.Hosting;
using Wolverine.RabbitMQ;

namespace CircuitBreakingTests;

public class Samples
{
    public static async Task no_filters()
    {
        #region sample_circuit_breaker_usage

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Handlers.OnException<InvalidOperationException>()
                    .Discard();

                opts.ListenToRabbitQueue("incoming")
                    .CircuitBreaker(cb =>
                    {
                        // Minimum number of messages encountered within the tracking period
                        // before the circuit breaker will be evaluated
                        cb.MinimumThreshold = 10;

                        // The time to pause the message processing before trying to restart
                        cb.PauseTime = 1.Minutes();

                        // The tracking period for the evaluation. Statistics tracking
                        cb.TrackingPeriod = 5.Minutes();

                        // If the failure percentage is higher than this number, trip
                        // the circuit and stop processing
                        cb.FailurePercentageThreshold = 10;

                        // Optional allow list
                        cb.Include<SqlException>(e => e.Message.Contains("Failure"));
                        cb.Include<SocketException>();

                        // Optional ignore list
                        cb.Exclude<InvalidOperationException>();
                    });
            }).StartAsync();

        #endregion
    }
}
