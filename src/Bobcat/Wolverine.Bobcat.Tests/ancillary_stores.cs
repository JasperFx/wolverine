using IntegrationTests;
using JasperFx;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Wolverine.Marten;

namespace Wolverine.Bobcat.Tests.AncillaryStores;

// GH-4977 (bobcat#504): a Marten host with its main store and an ancillary AddMartenStore<IInvoicingStore>(). The
// main store keeps Customers, the ancillary store Invoices, and a [MartenStore(typeof(IInvoicingStore))] handler
// reads the customer from the main store and starts an invoice in the ancillary one -- all in one specification.

public interface IInvoicingStore : IDocumentStore;

public record CustomerRegistered(string Name);
public record RaiseInvoice(Guid CustomerId, decimal Amount);
public record InvoiceRaised(string CustomerName, decimal Amount);
public record SendInvoice(string CustomerName);
public record InvoiceVoided(string Reason);

public class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public void Apply(CustomerRegistered e) => Name = e.Name;
}

public class Invoice
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = "";
    public decimal Amount { get; set; }
    public bool Voided { get; set; }

    public void Apply(InvoiceRaised e)
    {
        CustomerName = e.CustomerName;
        Amount = e.Amount;
    }

    public void Apply(InvoiceVoided e) => Voided = true;
}

[MartenStore(typeof(IInvoicingStore))]
public static class RaiseInvoiceHandler
{
    // Reads from the main store, appends to the ancillary one: the handler's own session is IInvoicingStore's
    public static async Task<(IStartStream, OutgoingMessages)> Handle(RaiseInvoice command, IDocumentStore main)
    {
        await using var query = main.QuerySession();
        var customer = await query.LoadAsync<Customer>(command.CustomerId)
                       ?? throw new InvalidOperationException($"No customer {command.CustomerId}");

        return (MartenOps.StartStream<Invoice>(Guid.CreateVersion7(), new InvoiceRaised(customer.Name, command.Amount)),
            [new SendInvoice(customer.Name)]);
    }
}

public static class SendInvoiceHandler
{
    public static void Handle(SendInvoice message)
    {
    }
}

public sealed class TwoStoreHost : IAsyncLifetime
{
    private const string Schema = "bobcat_ancillary";

    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Fresh schemas, so the high-water marks and the streams below are this run's alone
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            foreach (var schema in new[] { Schema, Schema + "_inv", Schema + "_inv_wolverine" })
            {
                await using var drop = new NpgsqlCommand($"drop schema if exists {schema} cascade", conn);
                await drop.ExecuteNonQueryAsync();
            }
        }

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = Schema;
                        m.DisableNpgsqlLogging = true;
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<Customer>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddMartenStore<IInvoicingStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = Schema + "_inv";
                        m.DisableNpgsqlLogging = true;
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<Invoice>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = Schema + "_inv_wolverine");

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RaiseInvoiceHandler))
                    .IncludeType(typeof(SendInvoiceHandler));
            })
            .StartAsync();

        await Host.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Host.Services.GetRequiredService<IInvoicingStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is null) return;
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class ancillary_stores(TwoStoreHost app) : WolverineSpec(app.Host), IClassFixture<TwoStoreHost>
{
    [Fact]
    public async Task a_handler_reads_the_main_store_and_appends_to_the_ancillary_one()
    {
        var theCustomer = Guid.CreateVersion7();

        // Routed by type: Customer is the main store's
        await GivenEvents<Customer>(theCustomer, new CustomerRegistered("Acme"));

        await WhenReceived(new RaiseInvoice(theCustomer, 100m));

        ThenEvents(new InvoiceRaised("Acme", 100m));
        ThenMessageSent<SendInvoice>();

        // Invoice is the ancillary store's, by type, or by name
        var theInvoice = TheStartedStream<Invoice>();
        (await ThenAggregate<Invoice>(theInvoice)).Amount.ShouldBe(100m);
        await In<IInvoicingStore>().ThenStreamIsStarted<Invoice>(theInvoice);

        // The tracked session heard the ancillary store commit, and says which store it was
        LastAct.Appended.Single().Store!.Name.ShouldBe("iinvoicingstore", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task an_act_through_a_view_is_the_specifications_act_and_the_message_steps_work_on_it()
    {
        var theCustomer = Guid.CreateVersion7();
        await GivenEvents<Customer>(theCustomer, new CustomerRegistered("Globex"));

        var invoicing = In<IInvoicingStore>();
        await invoicing.WhenReceived(new RaiseInvoice(theCustomer, 25m));

        // The same act, seen from the spec and from the view
        invoicing.ThenMessageSent<SendInvoice>();
        invoicing.ThenEvents(new InvoiceRaised("Globex", 25m));
        ThenEvents(new InvoiceRaised("Globex", 25m));
        LastAct.ShouldBeSameAs(invoicing.LastAct);
    }

    [Fact]
    public async Task an_act_outside_the_tracked_session_is_read_back_from_the_store_it_appended_to()
    {
        var theInvoice = Guid.CreateVersion7();
        await GivenEvents<Invoice>(theInvoice, new InvoiceRaised("Initech", 10m));

        // Straight to the ancillary store, through a session Wolverine never opened: the tracked session hears no
        // commit on it, so that store alone is read back from its high-water mark
        await WhenActing("an invoice is voided by hand", async services =>
        {
            await using var session = services.GetRequiredService<IInvoicingStore>().LightweightSession();
            session.Events.Append(theInvoice, new InvoiceVoided("duplicate"));
            await session.SaveChangesAsync();
        });

        ThenEvents(new InvoiceVoided("duplicate"));
        LastAct.EventTagsKnown.ShouldBeFalse();

        var voided = LastAct.NewEvents.Single();
        LastAct.StoreOf(voided)!.Identity.Name.ShouldBe("iinvoicingstore", StringCompareShould.IgnoreCase);
        (await ThenAggregate<Invoice>(theInvoice)).Voided.ShouldBeTrue();
    }

    [Fact]
    public async Task a_whole_store_failure_names_the_store_each_event_came_from()
    {
        var theInvoice = Guid.CreateVersion7();
        await GivenEvents<Invoice>(theInvoice, new InvoiceRaised("Umbrella", 10m));

        await WhenActing("an invoice is voided by hand", async services =>
        {
            await using var session = services.GetRequiredService<IInvoicingStore>().LightweightSession();
            session.Events.Append(theInvoice, new InvoiceVoided("duplicate"));
            await session.SaveChangesAsync();
        });

        // Bobcat's own whole-store step, on the outcome Wolverine's fallback built
        var failure = Should.Throw<Exception>(() => ThenEvents(new InvoiceVoided("a different reason")));
        failure.Message.ShouldContain("iinvoicingstore", Case.Insensitive);
    }
}
