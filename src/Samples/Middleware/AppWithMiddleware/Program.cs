using AppWithMiddleware;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Just letting Marten build out known database schema elements upfront
// Helps with Wolverine integration in development
builder.Services.AddResourceSetupOnStartup();

builder.Services.AddMarten(opts =>
{
    // This would be from your configuration file in typical usage
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "wolverine_middleware";
})
    // This is the wolverine integration for the outbox/inbox,
    // transactional middleware, saga persistence we don't care about
    // yet
    .IntegrateWithWolverine();

#region sample_registering_middleware_by_message_type

builder.Host.UseWolverine(opts =>
{
    // This middleware should be applied to all handlers where the
    // command type implements the IAccountCommand interface that is the
    // "detected" message type of the middleware
    opts.Policies.ForMessagesOfType<IAccountCommand>().AddMiddleware(typeof(AccountLookupMiddleware));

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

#endregion

var app = builder.Build();

app.MapControllers();

// One Minimal API that just delegates directly to Wolverine
app.MapPost("/accounts/debit", (DebitAccount command, IMessageBus bus) => bus.InvokeAsync(command));

// This is important, I'm opting into JasperFx to be my
// command line executor for extended options
return await app.RunJasperFxCommands(args);