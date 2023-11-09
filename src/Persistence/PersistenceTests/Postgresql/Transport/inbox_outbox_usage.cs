using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Postgresql.Transport;

[Collection("sqlserver")]
public class inbox_outbox_usage : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outbox").AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<SqlServerPing>().ToSqlServerQueue("ping");
                opts.PublishMessage<SqlServerPong>().ToSqlServerQueue("pong");
                opts.ListenToSqlServerQueue("pong");
                opts.ListenToSqlServerQueue("ping");
                
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_response_with_outbox()
    {
        var tracked = await _host.TrackActivity().WaitForMessageToBeReceivedAt<SqlServerPong>(_host).InvokeMessageAndWaitAsync(new SqlServerPing("first"));
        
        tracked.FindSingleTrackedMessageOfType<SqlServerPong>()
            .Name.ShouldBe("first");
    }

    [Fact]
    public async Task schedule_a_ping()
    {
        var tracked = await _host.TrackActivity()
            .WaitForMessageToBeReceivedAt<SqlServerPong>(_host)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(bus => bus.ScheduleAsync(new SqlServerPong("scheduled"), 3.Seconds()));
        
        tracked.FindSingleTrackedMessageOfType<SqlServerPong>()
            .Name.ShouldBe("scheduled");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }
}


public record SqlServerPing(string Name);
public record SqlServerPong(string Name);

public static class PingPongHandler
{
    [Transactional]
    public static SqlServerPong Handle(SqlServerPing ping, SqlConnection connection)
    {
        
        return new SqlServerPong(ping.Name);
    }

    public static void Handle(SqlServerPong pong, SqlConnection connection)
    {
        Debug.WriteLine("Got it");
    }
}