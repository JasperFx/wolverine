using System.Data.SqlClient;
using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

[Collection("Postgresql")]
public class inbox_outbox_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "outbox").AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<PostgresqlPing>().ToPostgresqlQueue("ping");
                opts.PublishMessage<PostgresqlPong>().ToPostgresqlQueue("pong");
                // GH-5736. An idle queue listener polls at DurabilitySettings.ScheduledJobPollingTime, 5 seconds by
                // default, and nothing wakes it sooner here. The cascaded pong lands just after the listener's first
                // poll at startup, so only the second poll picked it up, about 5 seconds later: the tracked session
                // took 4.9-5.0 seconds against its 5 second timeout
                opts.ListenToPostgresqlQueue("pong").PollingInterval(250.Milliseconds());
                opts.ListenToPostgresqlQueue("ping").PollingInterval(250.Milliseconds());

                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_response_with_outbox()
    {
        var tracked = await _host.TrackActivity().WaitForMessageToBeReceivedAt<PostgresqlPong>(_host).InvokeMessageAndWaitAsync(new PostgresqlPing("first"));

        tracked.FindSingleTrackedMessageOfType<PostgresqlPong>()
            .Name.ShouldBe("first");
    }

    [Fact]
    public async Task schedule_a_ping()
    {
        var tracked = await _host.TrackActivity()
            .WaitForMessageToBeReceivedAt<PostgresqlPong>(_host)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(bus => bus.ScheduleAsync(new PostgresqlPong("scheduled"), 3.Seconds()));

        tracked.FindSingleTrackedMessageOfType<PostgresqlPong>()
            .Name.ShouldBe("scheduled");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

public record PostgresqlPing(string Name);
public record PostgresqlPong(string Name);

public static class PingPongHandler
{
    [Transactional]
    public static PostgresqlPong Handle(PostgresqlPing ping, NpgsqlConnection connection)
    {

        return new PostgresqlPong(ping.Name);
    }

    public static void Handle(PostgresqlPong pong, NpgsqlConnection connection)
    {
        Debug.WriteLine("Got it");
    }
}