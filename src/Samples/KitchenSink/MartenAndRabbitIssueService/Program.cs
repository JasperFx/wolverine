using IntegrationTests;
using Marten;
using MartenAndRabbitIssueService;
using MartenAndRabbitMessages;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

#region sample_kitchen_sink_bootstrapping

var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyJasperFxExtensions();

builder.Host.UseWolverine(opts =>
{
    // I'm setting this up to publish to the same process
    // just to see things work
    opts.PublishAllMessages()
        .ToRabbitExchange("issue_events", exchange => exchange.BindQueue("issue_events"))
        .UseDurableOutbox();

    opts.ListenToRabbitQueue("issue_events").UseDurableInbox();

    opts.UseRabbitMq(factory =>
    {
        // Just connecting with defaults, but showing
        // how you *could* customize the connection to Rabbit MQ
        factory.HostName = "localhost";
        factory.Port = 5672;
    });
});

// This is actually important, this directs
// the app to build out all declared Postgresql and
// Rabbit MQ objects on start up if they do not already
// exist
builder.Services.AddResourceSetupOnStartup();

// Just pumping out a bunch of messages so we can see
// statistics
builder.Services.AddHostedService<Worker>();

builder.Services.AddMarten(opts =>
{
    // I think you would most likely pull the connection string from
    // configuration like this:
    // var martenConnectionString = builder.Configuration.GetConnectionString("marten");
    // opts.Connection(martenConnectionString);

    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "issues";

    // Just letting Marten know there's a document type
    // so we can see the tables and functions created on startup
    opts.RegisterDocumentType<Issue>();

    // I'm putting the inbox/outbox tables into a separate "issue_service" schema
}).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "issue_service");

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// Actually important to return the exit code here!
return await app.RunJasperFxCommands(args);

#endregion