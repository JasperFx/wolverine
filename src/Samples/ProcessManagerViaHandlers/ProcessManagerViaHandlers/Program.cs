using JasperFx;
using Marten;
using Marten.Events.Projections;
using ProcessManagerViaHandlers.OrderFulfillment;
using Wolverine;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString!);
        opts.DatabaseSchemaName = "process_manager";

        // Inline snapshot: OrderFulfillmentState is projected on every SaveChangesAsync
        // so that subsequent FetchForWriting calls see the latest state without a daemon.
        opts.Projections.Snapshot<OrderFulfillmentState>(SnapshotLifecycle.Inline);
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    // Wraps handler execution in a Marten session + SaveChangesAsync and wires the outbox.
    opts.Policies.AutoApplyTransactions();
});

var app = builder.Build();

// Thin HTTP surface so you can curl the sample or demo the process externally.
// The documentation focuses on the handlers, not the transport.
app.MapPost("/orders/start",
    (StartOrderFulfillment command, IMessageBus bus) => bus.InvokeAsync(command));

return await app.RunJasperFxCommands(args);

// Exposed as partial so Alba can bootstrap the host in the test project.
public partial class Program;
