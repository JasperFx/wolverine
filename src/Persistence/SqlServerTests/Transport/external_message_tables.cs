using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Spectre.Console;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Transport;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;
using Table = Weasel.SqlServer.Tables.Table;

namespace SqlServerTests.Transport;

public class external_message_tables : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("outside");
    }
    
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task can_create_basic_table()
    {
        var definition = new ExternalMessageTable( new DbObjectName("outside", "incoming1"))
        {
            MessageType = typeof(Message1)
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outside");
            }).StartAsync();

        var storage = host.Services.GetRequiredService<IMessageStore>()
            .As<SqlServerMessageStore>();

        var table = storage.AddExternalMessageTable(definition).ShouldBeOfType<Table>();
        table.Columns.Select(x => x.Name).ShouldBe(new string[]{"id", "body", "timestamp"});
        table.Columns.Select(x => x.Type).ShouldBe(new string[]{"uniqueidentifier", "varbinary(max)", "datetimeoffset"});
        table.PrimaryKeyColumns.Single().ShouldBe("id");
        

        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await table.MigrateAsync(conn);

        var delta = await table.FindDeltaAsync(conn);
        
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
        
    }
    
    [Fact]
    public async Task can_create_basic_table_with_message_type()
    {
        var definition = new ExternalMessageTable(new DbObjectName("outside", "incoming1"))
        {
            MessageType = typeof(Message1),
            MessageTypeColumnName = "message_type"
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outside");
            }).StartAsync();

        var storage = host.Services.GetRequiredService<IMessageStore>()
            .As<SqlServerMessageStore>();

        var table = storage.AddExternalMessageTable(definition).ShouldBeOfType<Table>();
        table.Columns.Select(x => x.Name).ShouldBe(new string[]{"id", "body", "timestamp", "message_type"});
        table.Columns.Select(x => x.Type).ShouldBe(new string[]{"uniqueidentifier", "varbinary(max)", "datetimeoffset", "varchar(250)"});
        table.PrimaryKeyColumns.Single().ShouldBe("id");
        

        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await table.MigrateAsync(conn);

        var delta = await table.FindDeltaAsync(conn);
        
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
        
    }

    [Fact]
    public async Task end_to_end_default_message_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outside");

                opts.ListenForMessagesFromExternalDatabaseTable("outside", "incoming1", table =>
                {
                    table.MessageType = typeof(Message1);
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message1>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("outside.incoming1", new Message1()));

        var envelope = tracked.Received.SingleEnvelope<Message1>();
        envelope.Destination.ShouldBe(new Uri("external-table://outside.incoming1/"));
    }
    
    [Fact]
    public async Task end_to_end_default_variable_message_types()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outside");

                opts.ListenForMessagesFromExternalDatabaseTable("outgoing", "incoming1", table =>
                {
                    table.MessageTypeColumnName = "message_type";
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message2>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("outgoing.incoming1", new Message2()));

        var envelope = tracked.Received.SingleEnvelope<Message2>();
        envelope.Destination.ShouldBe(new Uri("external-table://outgoing.incoming1/"));
    }

    [Fact]
    public async Task end_to_end_default_variable_message_types_customize_table_in_every_possible_way()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, "outside");

                opts.ListenForMessagesFromExternalDatabaseTable("outside", "incoming1", table =>
                {
                    table.IdColumnName = "pk";
                    table.TimestampColumnName = "added";
                    table.JsonBodyColumnName = "message_body";
                    table.MessageTypeColumnName = "message_kind";
                    
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message2>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("outside.incoming1", new Message2()));

        var envelope = tracked.Received.SingleEnvelope<Message2>();
        envelope.Destination.ShouldBe(new Uri("external-table://outside.incoming1/"));
    }
    
}

public static class Message1Handler
{
    public static void Handle(Message1 message)
    {
        Debug.WriteLine("Got a Message1");
    }
    
    public static void Handle(Message2 message)
    {
        Debug.WriteLine("Got a Message2");
    }
    
    public static void Handle(Message3 message)
    {
        Debug.WriteLine("Got a Message3");
    }
    
}