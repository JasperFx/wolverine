using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5893. Stopping a PostgreSQL queue listener waits for its scheduled-message worker, so nothing in that
/// worker can still use the data source after StopAsync returns. Removing a tenant (GH-5840) disposes that data
/// source right after the stop. The worker's pauses observe the stop, so an idle worker ends at once.
/// </summary>
public class stopping_waits_for_the_scheduled_worker_5893
{
    [Fact]
    public async Task stop_returns_only_after_the_scheduled_worker_has_finished()
    {
        // The worker sits in its first pause for an hour unless the stop ends it
        var settings = new DurabilitySettings { ScheduledJobFirstExecution = 1.Hours() };

        var transport = new PostgresqlTransport { AutoProvision = true };
        var queue = new PostgresqlQueue("gh5893", transport);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(settings);

        await using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        var listener = new PostgresqlQueueListener(queue, runtime, Substitute.For<IReceiver>(), dataSource, null);

        await listener.StartAsync();
        (listener.ScheduledWorker != null).ShouldBeTrue();

        await listener.StopAsync();

        listener.ScheduledWorker!.IsCompleted.ShouldBeTrue();
    }
}
