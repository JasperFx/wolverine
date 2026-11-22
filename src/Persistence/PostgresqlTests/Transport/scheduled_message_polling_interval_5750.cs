using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5750. The scheduled message loop (move due scheduled messages into the queue, delete expired
/// rows) can poll on its own interval, so a low PollingInterval for fast pickup does not also make
/// that loop run at the same rate. Nothing here touches the database.
/// </summary>
public class scheduled_message_polling_interval_5750
{
    private static readonly DurabilitySettings theSettings = new() { ScheduledJobPollingTime = 7.Seconds() };

    private readonly PostgresqlQueue theQueue = new("gh5750", new PostgresqlTransport());

    [Fact]
    public void scheduled_message_polling_interval_defaults_to_null()
    {
        theQueue.ScheduledMessagePollingInterval.ShouldBeNull();
    }

    [Fact]
    public void the_fluent_interface_sets_the_scheduled_message_polling_interval()
    {
        var expression = new PostgresqlListenerConfiguration(theQueue)
            .PollingInterval(100.Milliseconds())
            .ScheduledMessagePollingInterval(3.Seconds());

        ((IDelayedEndpointConfiguration)expression).Apply();

        theQueue.ScheduledMessagePollingInterval.ShouldBe(3.Seconds());
        theQueue.PollingInterval.ShouldBe(100.Milliseconds());
    }

    [Fact]
    public void the_listener_uses_the_configured_scheduled_message_polling_interval()
    {
        theQueue.PollingInterval = 100.Milliseconds();
        theQueue.ScheduledMessagePollingInterval = 3.Seconds();

        resolvedScheduledPollingInterval(theQueue).ShouldBe(3.Seconds());
    }

    [Fact]
    public void the_listener_falls_back_to_the_polling_interval()
    {
        theQueue.PollingInterval = 100.Milliseconds();

        resolvedScheduledPollingInterval(theQueue).ShouldBe(100.Milliseconds());
    }

    [Fact]
    public void the_listener_falls_back_to_the_scheduled_job_polling_time()
    {
        resolvedScheduledPollingInterval(theQueue).ShouldBe(theSettings.ScheduledJobPollingTime);
    }

    private static TimeSpan resolvedScheduledPollingInterval(PostgresqlQueue queue)
    {
        using var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        var listener = new PostgresqlQueueListener(queue, runtime, Substitute.For<IReceiver>(), dataSource, null);
        return listener.ScheduledPollingInterval;
    }

    private static IWolverineRuntime runtime
    {
        get
        {
            var runtime = Substitute.For<IWolverineRuntime>();
            runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
            runtime.DurabilitySettings.Returns(theSettings);
            return runtime;
        }
    }
}
