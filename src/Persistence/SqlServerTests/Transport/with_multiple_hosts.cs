using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Shouldly;

namespace SqlServerTests.Transport;

[Collection("sqlserver")]
public class with_multiple_hosts : IAsyncLifetime
{
    private IHost _sender;
    private IHost _listener;

    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "sender","transport")
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<SqlServerFoo>().ToSqlServerQueue("foobar");
                opts.PublishMessage<SqlServerBar>().ToSqlServerQueue("foobar");
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();

            }).StartAsync();
        _listener = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "listener","transport")
                    .AutoProvision()
                    .AutoPurgeOnStartup();
                opts.PublishMessage<SqlServerBar>().ToSqlServerQueue("foobar");
                opts.ListenToSqlServerQueue("foobar");
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FooBarHandler>();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_response_handled_by_listener()
    {
        var tracked  = await _sender.TrackActivity()
            .Timeout(30.Seconds())
            .AlsoTrack(_listener)
            .SendMessageAndWaitAsync(new SqlServerFoo("first"));
        tracked.Sent.SingleMessage<SqlServerFoo>().Name.ShouldBe("first");
        tracked.Received.SingleMessage<SqlServerFoo>().Name.ShouldBe("first");    
        tracked.Received.SingleMessage<SqlServerBar>().Name.ShouldBe("first");    
    }
    
    [Fact]
    public async Task cascaded_response_not_handled_by_sender()
    {
        var tracked  = await _sender.TrackActivity()
            .SendMessageAndWaitAsync(new SqlServerFoo("first"));
        tracked.Sent.SingleMessage<SqlServerFoo>().Name.ShouldBe("first");
        tracked.Received.MessagesOf<SqlServerFoo>().Count().ShouldBe(0);
        tracked.Received.MessagesOf<SqlServerBar>().Count().ShouldBe(0);    
            
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _listener.StopAsync();
        _listener.Dispose();
    }
}

public record SqlServerFoo(string Name);
public record SqlServerBar(string Name);

public class FooBarHandler
{
    
    public SqlServerBar Handle(SqlServerFoo foo)
    {
        Debug.WriteLine($"Handling foo {foo.Name}");
        return new SqlServerBar(foo.Name);
    }

    public void Handle(SqlServerBar bar, SqlConnection connection)
    {
        Debug.WriteLine($"Handling bar {bar.Name}");
    }
}