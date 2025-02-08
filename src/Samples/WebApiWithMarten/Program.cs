using Marten;
using Marten.AspNetCore;
using JasperFx;
using WebApiWithMarten;
using Wolverine;
using Wolverine.Marten;

#region sample_integrating_wolverine_with_marten

var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
    {
        var connectionString = builder
            .Configuration
            .GetConnectionString("postgres");

        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })
    // Optionally add Marten/Postgresql integration
    // with Wolverine's outbox
    .IntegrateWithWolverine();

// You can also place the Wolverine database objects
// into a different database schema, in this case
// named "wolverine_messages"
//.IntegrateWithWolverine("wolverine_messages");

builder.Host.UseWolverine(opts =>
{
    // I've added persistent inbox
    // behavior to the "important"
    // local queue
    opts.LocalQueue("important")
        .UseDurableInbox();
});

#endregion

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


var app = builder.Build();


app.MapGet("/orders", (IQuerySession session, HttpContext context)
    => session.Query<Order>().WriteArray(context));

#region sample_delegate_to_command_bus_from_minimal_api

// Delegate directly to Wolverine commands -- More efficient recipe coming later...
app.MapPost("/orders/create2", (CreateOrder command, IMessageBus bus)
    => bus.InvokeAsync(command));

#endregion


#region sample_create_order_through_minimal_api

app.MapPost("/orders/create3", async (CreateOrder command, IDocumentSession session, IMartenOutbox outbox) =>
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Marten
    session.Store(order);

    // Don't worry, this message doesn't go out until
    // after the Marten transaction succeeds
    await outbox.PublishAsync(new OrderCreated(order.Id));

    // Commit the Marten transaction
    await session.SaveChangesAsync();
});

#endregion

app.MapGet("/", () => Results.Redirect("/swagger"));

app.UseSwagger();
app.UseSwaggerUI();

// Lot of Wolverine and Marten diagnostics and administrative tools
// come through JasperFx command line support
return await app.RunJasperFxCommands(args);