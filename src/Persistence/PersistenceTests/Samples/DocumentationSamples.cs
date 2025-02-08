using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Transports.Tcp;

namespace PersistenceTests.Samples;

public class DocumentationSamples
{
    #region sample_programmatic_management_of_message_storage

    // IHost would be your application in a testing harness
    public static async Task testing_setup_or_teardown(IHost host)
    {
        // Programmatically apply any outstanding message store
        // database changes
        await host.SetupResources();

        // Teardown the database message storage
        await host.TeardownResources();

        // Clear out any database message storage
        // also tries to clear out any messages held
        // by message brokers connected to your Wolverine app
        await host.ResetResourceState();

        var store = host.Services.GetRequiredService<IMessageStore>();

        // Rebuild the database schema objects
        // and delete existing message data
        // This is good for testing
        await store.Admin.RebuildAsync();

        // Remove all persisted messages
        await store.Admin.ClearAllAsync();
    }

    #endregion

    public static async Task configure_all_subscribers_as_durable()
    {
        #region sample_make_all_subscribers_be_durable

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This forces every outgoing subscriber to use durable
                // messaging
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_one_subscribers_as_durable()
    {
        #region sample_make_specific_subscribers_be_durable

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(5555)

                    // This option makes just this one outgoing subscriber use
                    // durable message storage
                    .UseDurableOutbox();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_inbox_on_listeners()
    {
        #region sample_configuring_durable_inbox

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(5555)

                    // Make specific endpoints be enrolled
                    // in the durable inbox
                    .UseDurableInbox();

                // Make every single listener endpoint use
                // durable message storage
                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_local_subscribers()
    {
        #region sample_durable_local_queues

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.UseDurableLocalQueues();

                // or

                opts.LocalQueue("important").UseDurableInbox();

                // or conventionally, make the local queues for messages in a certain namespace
                // be durable
                opts.Policies.ConfigureConventionalLocalRouting().CustomizeQueues((type, queue) =>
                {
                    if (type.IsInNamespace("MyApp.Commands.Durable"))
                    {
                        queue.UseDurableInbox();
                    }
                });
            }).StartAsync();

        #endregion
    }

    public static async Task<int> SetupSqlServer(string[] args)
    {
        #region sample_setup_sqlserver_storage

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("sqlserver");

        builder.Host.UseWolverine(opts =>
        {
            // Setting up Sql Server-backed message storage
            // This requires a reference to Wolverine.SqlServer
            opts.PersistMessagesWithSqlServer(connectionString);

            // Other Wolverine configuration
        });

        // This is rebuilding the persistent storage database schema on startup
        // and also clearing any persisted envelope state
        builder.Host.UseResourceSetupOnStartup();

        var app = builder.Build();

        // Other ASP.Net Core configuration...

        // Using JasperFx opens up command line utilities for managing
        // the message storage
        return await app.RunJasperFxCommands(args);

        #endregion
    }

    public static async Task<int> SetupPostgresql(string[] args)
    {
        #region sample_setup_postgresql_storage

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("postgres");

        builder.Host.UseWolverine(opts =>
        {
            // Setting up Postgresql-backed message storage
            // This requires a reference to Wolverine.Postgresql
            opts.PersistMessagesWithPostgresql(connectionString);

            // Other Wolverine configuration
        });

        // This is rebuilding the persistent storage database schema on startup
        // and also clearing any persisted envelope state
        builder.Host.UseResourceSetupOnStartup();

        var app = builder.Build();

        // Other ASP.Net Core configuration...

        // Using JasperFx opens up command line utilities for managing
        // the message storage
        return await app.RunJasperFxCommands(args);

        #endregion
    }

    public static async Task configure_inbox_keeping()
    {
        #region sample_configuring_KeepAfterMessageHandling

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The default is 5 minutes, but if you want to keep
                // messages around longer (or shorter) in case of duplicates,
                // this is how you do it
                opts.Durability.KeepAfterMessageHandling = 10.Minutes();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_persistence_metrics()
    {
        #region sample_configuring_persistence_metrics

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This does assume that you have *some* kind of message
                // persistence set up
                
                // This is enabled by default, but just showing that
                // you *could* disable it
                opts.Durability.DurabilityMetricsEnabled = true;

                // The default is 5 seconds, but maybe you want it slower
                // because this does have to do a non-trivial query
                opts.Durability.UpdateMetricsPeriod = 10.Seconds();
            }).StartAsync();

        #endregion
    }
    
    
    public static async Task options_important_for_modular_monolith()
    {
        #region sample_important_settings_for_modular_monoliths

        var builder = Host.CreateApplicationBuilder();

        // It's not important that it's Marten here, just that if you have
        // *any* message persistence configured for the transactional inbox/outbox
        // support, it's impacted by the MessageIdentity setting
        builder.Services.AddMarten(opts =>
            {
                var connectionString = builder.Configuration.GetConnectionString("marten");
                opts.Connection(connectionString);
            })
            
            // This line of code is adding a PostgreSQL backed transactional inbox/outbox 
            // integration using the same database as what Marten is using
            .IntegrateWithWolverine();
        
        builder.UseWolverine(opts =>
        {
            // Tell Wolverine that when you have more than one handler for the same
            // message type, they should be executed separately and automatically
            // "stuck" to separate local queues
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;


            // *If* you may be using external message brokers in such a way that they
            // are "fanning out" a single message sent from an upstream system into
            // multiple listeners within the same Wolverine process, you'll want to make
            // this setting to tell Wolverine to treat that as completely different messages
            // instead of failing by idempotency checks
            opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
            
            // Not 100% necessary for "modular monoliths", but this makes the Wolverine durable
            // inbox/outbox feature a lot easier to use and DRYs up your message handlers
            opts.Policies.AutoApplyTransactions();
        });

        #endregion
    }
}