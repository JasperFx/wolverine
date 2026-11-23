using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Transport;
using Wolverine.Transports;
using Xunit;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-5893. Stopping a SQL Server queue listener waits for its scheduled-message worker, so nothing in that
/// worker can still use the database after StopAsync returns. The worker's pauses observe the stop, so an idle
/// worker ends at once instead of sleeping out its interval.
/// </summary>
public class stopping_waits_for_the_scheduled_worker_5893
{
    [Fact]
    public async Task stop_returns_only_after_the_scheduled_worker_has_finished()
    {
        // The worker sits in its first pause for an hour unless the stop ends it
        var settings = new DurabilitySettings { ScheduledJobFirstExecution = 1.Hours() };

        var transport = new SqlServerTransport(new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString, SchemaName = "transport"
        }) { AutoProvision = true };
        var queue = new SqlServerQueue("gh5893", transport);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(settings);

        var listener = new SqlServerQueueListener(queue, runtime, Substitute.For<IReceiver>(),
            Servers.SqlServerConnectionString, null);

        await listener.StartAsync();
        (listener.ScheduledWorker != null).ShouldBeTrue();

        await listener.StopAsync();

        listener.ScheduledWorker!.IsCompleted.ShouldBeTrue();
    }
}
