// See https://aka.ms/new-console-template for more information

using IntegrationTests;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using LoadTesting;
using LoadTesting.Trips;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.Transports;

return await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.UseDurableLocalQueues();

        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "load_testing";

            m.DisableNpgsqlLogging = true;
            
            m.Schema.For<RepairWork>();
            
            m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
            m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
            m.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
        }).AddAsyncDaemon(DaemonMode.Solo).IntegrateWithWolverine();
        
        opts.ServiceName = "TripPublisher";
                
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.ApplicationAssembly = typeof(Program).Assembly;
        opts.EnableAutomaticFailureAcks = false;
        
        opts.Policies.AutoApplyTransactions();

        opts.Services.AddSingleton<Publisher>();
        
        // Force it to use Rabbit MQ
        opts.Policies.DisableConventionalLocalRouting();
        opts.UseRabbitMq().UseConventionalRouting().AutoProvision();
        
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

        opts.LocalQueueFor<ContinueTrip>().UseDurableInbox(new BufferingLimits(50, 20));
        
        //opts.Services.AddHostedService<KickOffPublishing>();
        opts.Services.AddResourceSetupOnStartup();
        
        opts.Policies.AllLocalQueues(listener =>
        {
            listener.UseDurableInbox();
            listener.Sequential();
        });

        
    }).RunJasperFxCommands(args);