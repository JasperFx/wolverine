using System.Threading.Tasks;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Transports.Tcp;

namespace PersistenceTests.Samples;

public class DocumentationSamples
{

    
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

        // Using Oakton opens up command line utilities for managing
        // the message storage
        return await app.RunOaktonCommands(args);

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

        // Using Oakton opens up command line utilities for managing
        // the message storage
        return await app.RunOaktonCommands(args);

        #endregion
    }
}