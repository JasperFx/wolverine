using AppWithMiddleware;
using IntegrationTests;
using Marten;
using Oakton;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
{
    // This would be from your configuration file in typical usage
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "wolverine_middleware";
})
    // This is the wolverine integration for the outbox/inbox,
    // transactional middleware, saga persistence we don't care about
    // yet
    .IntegrateWithWolverine()
    
    // Just letting Marten build out known database schema elements upfront
    // Helps with Wolverine integration in development
    .ApplyAllDatabaseChangesOnStartup();

builder.Host.UseWolverine(opts =>
{
    // Middleware introduced in previous posts
    opts.Handlers.AddMiddlewareByMessageType(typeof(AccountLookupMiddleware));
    opts.UseFluentValidation();

    // Explicit routing for the AccountUpdated
    // message handling
    opts.PublishMessage<AccountUpdated>()
        .ToLocalQueue("signalr")

        // Not durable
        .BufferedInMemory();
    
    var rabbitUri = builder.Configuration.GetValue<Uri>("rabbitmq-broker-uri");
    opts.UseRabbitMq(rabbitUri)
        // Just do the routing off of conventions, more or less
        // queue and/or exchange based on the Wolverine message type name
        .UseConventionalRouting()
        .ConfigureSenders(x => x.UseDurableOutbox());

});

var app = builder.Build();

// One Minimal API that just delegates directly to Wolverine
app.MapPost("/accounts/debit", (DebitAccount command, IMessageBus bus) => bus.InvokeAsync(command));

return await app.RunOaktonCommands(args);