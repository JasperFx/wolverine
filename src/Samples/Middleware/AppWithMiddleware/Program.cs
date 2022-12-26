using AppWithMiddleware;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Oakton;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Marten;

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
    // message handling. This has precedence over conventional routing
    opts.PublishMessage<AccountUpdated>()
        .ToLocalQueue("signalr")

        // Throw the message away if it's not successfully
        // delivered within 10 seconds
        .DeliverWithin(10.Seconds())
        
        // Not durable
        .BufferedInMemory();
});

var app = builder.Build();

// One Minimal API that just delegates directly to Wolverine
app.MapPost("/accounts/debit", (DebitAccount command, IMessageBus bus) => bus.InvokeAsync(command));

// This is important, I'm opting into Oakton to be my
// command line executor for extended options
return await app.RunOaktonCommands(args);