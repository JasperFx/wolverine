using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.RDBMS.Transport;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

public class external_message_tables : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("external");
    }
    
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task can_create_basic_table()
    {
        var definition = new ExternalMessageTable(new DbObjectName("external", "incoming1"))
        {
            MessageType = typeof(Message1)
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");
                
                opts.Policies.UseDurableLocalQueues();
            }).StartAsync();

        var storage = host.Services.GetRequiredService<IMessageStore>()
            .As<PostgresqlMessageStore>();

        var table = storage.AddExternalMessageTable(definition).ShouldBeOfType<Table>();
        table.Columns.Select(x => x.Name).ShouldBe(new string[]{"id", "body", "timestamp"});
        table.Columns.Select(x => x.Type).ShouldBe(new string[]{"uuid", "jsonb", "timestamp with time zone"});
        table.PrimaryKeyColumns.Single().ShouldBe("id");
        

        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await table.MigrateAsync(conn);

        var delta = await table.FindDeltaAsync(conn);
        
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
        
    }
    
    [Fact]
    public async Task can_create_basic_table_with_message_type()
    {
        var definition = new ExternalMessageTable(new DbObjectName("external", "incoming1"))
        {
            MessageType = typeof(Message1),
            MessageTypeColumnName = "message_type"
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");
            }).StartAsync();

        var storage = host.Services.GetRequiredService<IMessageStore>()
            .As<PostgresqlMessageStore>();

        var table = storage.AddExternalMessageTable(definition).ShouldBeOfType<Table>();
        table.Columns.Select(x => x.Name).ShouldBe(new string[]{"id", "body", "timestamp", "message_type"});
        table.Columns.Select(x => x.Type).ShouldBe(new string[]{"uuid", "jsonb", "timestamp with time zone", "varchar"});
        table.PrimaryKeyColumns.Single().ShouldBe("id");
        

        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
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
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");

                opts.ListenForMessagesFromExternalDatabaseTable("external", "incoming1", table =>
                {
                    table.MessageType = typeof(Message1);
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message1>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("external.incoming1", new Message1()));

        var envelope = tracked.Received.SingleEnvelope<Message1>();
        envelope.Destination.ShouldBe(new Uri("external-table://external.incoming1/"));
    }
    
        
    [Fact]
    public async Task end_to_end_default_variable_message_types()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");

                opts.ListenForMessagesFromExternalDatabaseTable("external", "incoming1", table =>
                {
                    table.MessageTypeColumnName = "message_type";
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message2>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("external.incoming1", new Message2()));

        var envelope = tracked.Received.SingleEnvelope<Message2>();
        envelope.Destination.ShouldBe(new Uri("external-table://external.incoming1/"));
    }
    
    [Fact]
    public async Task end_to_end_default_variable_message_types_customize_table_in_every_possible_way()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");

                opts.ListenForMessagesFromExternalDatabaseTable("external", "incoming1", table =>
                {
                    table.IdColumnName = "pk";
                    table.TimestampColumnName = "added";
                    table.JsonBodyColumnName = "message_body";
                    table.MessageTypeColumnName = "message_kind";
                    
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        var tracked = await host.TrackActivity().Timeout(1.Minutes()).WaitForMessageToBeReceivedAt<Message2>(host).ExecuteAndWaitAsync(
            _ => host.SendMessageThroughExternalTable("external.incoming1", new Message2()));

        var envelope = tracked.Received.SingleEnvelope<Message2>();
        envelope.Destination.ShouldBe(new Uri("external-table://external.incoming1/"));
    }

    [Fact]
    public async Task pull_in_message_that_goes_to_dead_letter_queue_and_replay_it()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "external");

                opts.ListenForMessagesFromExternalDatabaseTable("external", "incoming4", table =>
                {
                    table.IdColumnName = "pk";
                    table.TimestampColumnName = "added";
                    table.JsonBodyColumnName = "message_body";
                    table.MessageType = typeof(BlowsUpMessage);
                    table.PollingInterval = 1.Seconds();
                });

            }).StartAsync();

        // Rig it up to fail
        var waiter = BlowsUpMessageHandler.WaiterForCall(true);

        await host.SendMessageThroughExternalTable("external.incoming4", new BlowsUpMessage());
        var storage = host.GetRuntime().Storage;
        Guid[] ids = new Guid[0];
        while (!ids.Any())
        {
            var queued = await storage.DeadLetters.QueryDeadLetterEnvelopesAsync(new DeadLetterEnvelopeQueryParameters());
            ids = queued.DeadLetterEnvelopes.Select(x => x.Envelope.Id).ToArray();
        }
        
        // need to reset it
        var dlq = BlowsUpMessageHandler.WaiterForCall(false);
        await storage.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(ids);
        await dlq;
        BlowsUpMessageHandler.LastReceived.ShouldNotBeNull();
    }

}

public static class Bootstrapping
{
    public static void Configure()
    {
        #region sample_configuring_external_database_messaging

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(builder.Configuration.GetConnectionString("postgres"));

            // Or
            // opts.UseSqlServerPersistenceAndTransport(builder.Configuration.GetConnectionString("sqlserver"));
            
            // Or
            // opts.Services
            //     .AddMarten(builder.Configuration.GetConnectionString("postgres"))
            //     .IntegrateWithWolverine();
            
            // Directing Wolverine to "listen" for messages in an externally controlled table
            // You have to explicitly tell Wolverine about the schema name and table name
            opts.ListenForMessagesFromExternalDatabaseTable("exports", "messaging", table =>
                {
                    // The primary key column for this table, default is "id"
                    table.IdColumnName = "id";

                    // What column has the actual JSON data? Default is "json"
                    table.JsonBodyColumnName = "body";

                    // Optionally tell Wolverine that the message type name is this
                    // column. 
                    table.MessageTypeColumnName = "message_type";

                    // Add a column for the current time when a message was inserted
                    // Strictly for diagnostics
                    table.TimestampColumnName = "added";

                    // How often should Wolverine poll this table? Default is 10 seconds
                    table.PollingInterval = 1.Seconds();

                    // Maximum number of messages that each node should try to pull in at 
                    // any one time. Default is 100
                    table.MessageBatchSize = 50;

                    // Is Wolverine allowed to try to apply automatic database migrations for this
                    // table at startup time? Default is true.
                    // Also overridden by WolverineOptions.AutoBuildMessageStorageOnStartup
                    table.AllowWolverineControl = true;

                    // Wolverine uses a database advisory lock so that only one node at a time
                    // can ever be polling for messages at any one time. Default is 12000
                    // It might release contention to vary the advisory lock if you have multiple
                    // incoming tables or applications targeting the same database
                    table.AdvisoryLock = 12001;
                    
                    // Tell Wolverine what the default message type is coming from this
                    // table to aid in deserialization
                    table.MessageType = typeof(ExternalMessage);
                    
                    
                })
                
                // Just showing that you have all the normal options for configuring and
                // fine tuning the behavior of a message listening endpoint here
                .Sequential();
        });

        #endregion
    }
}

public class ExternalMessage;

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

public record BlowsUpMessage;

public static class BlowsUpMessageHandler
{
    public static TaskCompletionSource Waiter { get; private set; } = new();
    
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().MoveToErrorQueue();
    }
    
    public static bool WillBlowUp { get; set; } = true;

    public static Task WaiterForCall(bool shouldThrow)
    {
        LastReceived = null;
        WillBlowUp = shouldThrow;
        Waiter = new TaskCompletionSource();
        return Waiter.Task;
    }

    public static void Handle(BlowsUpMessage message)
    {
        if (WillBlowUp)
        {
            throw new Exception("You stink!");
        }
        
        LastReceived = message;
        Waiter.SetResult();
    }

    public static BlowsUpMessage LastReceived { get; set; }
}