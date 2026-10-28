using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.RabbitMQ;
using Xunit;

namespace DocumentationSamples.BestPractices;

// Supporting types for the Best Practices samples

public class Invoice
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public bool IsPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

public class InvoiceCustomer
{
    public Guid Id { get; set; }
    public bool SendPaymentReceipts { get; set; }
    public string Email { get; set; } = null!;
}

public record InvoicePaid(Guid InvoiceId, DateTimeOffset PaidAt);
public record SendPaymentReceipt(Guid InvoiceId, string Email);
public record InvoiceWasAlreadyPaid(Guid InvoiceId);

public class UnknownInvoiceException(Guid invoiceId) : Exception($"Unknown invoice {invoiceId}");

#region sample_bp_compound_handler

public record RecordPayment(Guid InvoiceId, decimal Amount, DateTimeOffset PaidAt);

public static class RecordPaymentHandler
{
    // Wolverine calls this first by naming convention. Its only job is to
    // "set the table" for the main handler by loading the data it needs.
    // Note the single round trip for both documents with Marten's Include()
    public static async Task<(Invoice, InvoiceCustomer)> LoadAsync(
        RecordPayment command,
        IDocumentSession session,
        CancellationToken cancellation)
    {
        InvoiceCustomer? customer = null;
        var invoice = await session
            .Query<Invoice>()
            .Include<InvoiceCustomer>(x => x.CustomerId, c => customer = c)
            .Where(x => x.Id == command.InvoiceId)
            .FirstOrDefaultAsync(token: cancellation);

        if (invoice == null) throw new UnknownInvoiceException(command.InvoiceId);

        return (invoice, customer!);
    }

    // The main handler is a synchronous pure function. It receives the loaded
    // data as method arguments, makes decisions, and *describes* the outcome
    // through its return values. Wolverine does the rest.
    public static (IStorageAction<Invoice>, OutgoingMessages) Handle(
        RecordPayment command,
        Invoice invoice,
        InvoiceCustomer customer)
    {
        var messages = new OutgoingMessages();

        if (invoice.IsPaid)
        {
            // Nothing to persist, but somebody might care
            messages.Add(new InvoiceWasAlreadyPaid(invoice.Id));
            return (Storage.Nothing<Invoice>(), messages);
        }

        invoice.IsPaid = true;
        invoice.PaidAt = command.PaidAt;

        messages.Add(new InvoicePaid(invoice.Id, command.PaidAt));

        if (customer.SendPaymentReceipts)
        {
            messages.Add(new SendPaymentReceipt(invoice.Id, customer.Email));
        }

        return (Storage.Update(invoice), messages);
    }
}

#endregion

#region sample_bp_compound_handler_unit_test

public class RecordPaymentHandlerTests
{
    [Fact]
    public void paying_an_open_invoice_updates_it_and_publishes_invoice_paid()
    {
        // No database, no mocks, no host. Just inputs and outputs.
        var invoice = new Invoice { Id = Guid.NewGuid(), Amount = 100 };
        var customer = new InvoiceCustomer { Id = Guid.NewGuid(), SendPaymentReceipts = true, Email = "jane@acme.com" };
        var command = new RecordPayment(invoice.Id, 100, DateTimeOffset.UtcNow);

        var (action, messages) = RecordPaymentHandler.Handle(command, invoice, customer);

        action.ShouldBeOfType<Update<Invoice>>().Entity.IsPaid.ShouldBeTrue();
        messages.ShouldContain(x => x is InvoicePaid);
        messages.ShouldContain(x => x is SendPaymentReceipt);
    }

    [Fact]
    public void paying_an_already_paid_invoice_changes_nothing()
    {
        var invoice = new Invoice { Id = Guid.NewGuid(), Amount = 100, IsPaid = true };
        var customer = new InvoiceCustomer { Id = Guid.NewGuid() };
        var command = new RecordPayment(invoice.Id, 100, DateTimeOffset.UtcNow);

        var (action, messages) = RecordPaymentHandler.Handle(command, invoice, customer);

        action.ShouldBeOfType<Nothing<Invoice>>();
        messages.Single().ShouldBeOfType<InvoiceWasAlreadyPaid>();
    }
}

#endregion

#region sample_bp_entity_attribute

public record CancelInvoice(Guid InvoiceId, string Reason);
public record InvoiceCancelled(Guid InvoiceId, string Reason);

public static class CancelInvoiceHandler
{
    // [Entity] tells Wolverine to load the Invoice by the InvoiceId member of
    // the command. If it's missing, Wolverine logs that and stops processing
    // before this method is ever called, so the handler is left with only
    // the happy path.
    public static (Delete<Invoice>, InvoiceCancelled) Handle(
        CancelInvoice command,
        [Entity] Invoice invoice)
    {
        return (Storage.Delete(invoice), new InvoiceCancelled(invoice.Id, command.Reason));
    }
}

#endregion

#region sample_bp_side_effect

// A side effect is a return value that *describes* an action against
// infrastructure. Wolverine finds the Execute/ExecuteAsync method and
// calls it after the handler, with whatever services that method needs.
public class WriteReceiptFile : ISideEffect
{
    public WriteReceiptFile(string fileName, string contents)
    {
        FileName = fileName;
        Contents = contents;
    }

    public string FileName { get; }
    public string Contents { get; }

    public Task ExecuteAsync(ReceiptSettings settings, CancellationToken token)
    {
        var path = Path.Combine(settings.Directory, FileName);
        return File.WriteAllTextAsync(path, Contents, token);
    }
}

public class ReceiptSettings
{
    public string Directory { get; set; } = Environment.CurrentDirectory;
}

public static class SendPaymentReceiptHandler
{
    // Pure function: easy to unit test by asserting on the WriteReceiptFile
    // that comes back out, with no file system anywhere in sight
    public static WriteReceiptFile Handle(SendPaymentReceipt message)
    {
        return new WriteReceiptFile($"{message.InvoiceId}.txt", $"Receipt sent to {message.Email}");
    }
}

#endregion

#region sample_bp_cascading_from_the_root

public record ApproveOrder(Guid OrderId);
public record OrderApproved(Guid OrderId);

public interface IApprovalRules
{
    bool CanAutoApprove(ApproveOrder command);
}

public static class ApproveOrderHandler
{
    // Preferred: the outgoing message is a return value, so anybody reading
    // this handler can see exactly what it might publish
    public static OrderApproved? Handle(ApproveOrder command, IApprovalRules rules)
    {
        // Use services to *decide*, then return the decision
        return rules.CanAutoApprove(command)
            ? new OrderApproved(command.OrderId)
            : null; // null means "no cascading message"
    }
}

public static class ApproveOrderExplicitHandler
{
    // Also fine if you prefer the explicit style. The side effect is still
    // visible from the root handler method
    public static async Task HandleAsync(ApproveOrder command, IApprovalRules rules, IMessageBus bus)
    {
        if (rules.CanAutoApprove(command))
        {
            await bus.PublishAsync(new OrderApproved(command.OrderId));
        }
    }
}

#endregion

#region sample_bp_guard_clause_discard

public class LicenseRequiredException : Exception;

public static class LicenseGuard
{
    public static bool IsOperationAllowed() => true;
}

public record GenerateReport(Guid ReportId);

public static class GenerateReportHandler
{
    public static void Handle(GenerateReport command)
    {
        // Don't do this. The message is silently consumed, nothing is logged,
        // and nobody ever finds out why reports stopped being generated:
        //
        // if (!LicenseGuard.IsOperationAllowed()) return;

        // Do this instead, and let the error policy below decide what happens
        if (!LicenseGuard.IsOperationAllowed())
        {
            throw new LicenseRequiredException();
        }

        // generate the report...
    }
}

public static class GuardClauseConfiguration
{
    public static async Task configure()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Retrying can never help, so just discard the message.
                // Wolverine logs the discard, and the exception shows up
                // in tracked session test output
                opts.Policies.OnException<LicenseRequiredException>().Discard();
            }).StartAsync();
    }
}

#endregion

public class SystemIsCompletelyUnusableException : Exception;
public class InvalidMessageYouWillNeverBeAbleToProcessException : Exception;

public static class ErrorHandlingByCategory
{
    #region sample_bp_error_handling_by_category

    public static async Task configure()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // 1. Transient or concurrency errors. Retry a few times with a
                //    short, growing cooldown. Jitter keeps a cluster of nodes
                //    from retrying in lockstep against a recovering database.
                //    If that doesn't work, keep the message around for a human.
                opts.Policies.OnException<TimeoutException>()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
                    .WithFullJitter()
                    .Then.MoveToErrorQueue();

                // 2. A dependency is down. Retrying every message one at a time
                //    just floods your logs and your dead letter queue. Put the
                //    message back and pause the whole listener instead.
                opts.Policies.OnException<SystemIsCompletelyUnusableException>()
                    .Requeue().AndPauseProcessing(5.Minutes());

                // 3. A message that can never succeed. Don't retry, don't pause,
                //    just get it out of the way. Use MoveToErrorQueue() instead
                //    if somebody might want to look at it later.
                opts.Policies.OnException<InvalidMessageYouWillNeverBeAbleToProcessException>()
                    .Discard();

                // 4. A distressed resource. If a high percentage of *all* messages
                //    on an endpoint are failing, trip a circuit breaker and give
                //    the resource a chance to recover
                opts.ListenToRabbitQueue("incoming")
                    .CircuitBreaker(cb =>
                    {
                        cb.MinimumThreshold = 10;
                        cb.TrackingPeriod = 5.Minutes();
                        cb.FailurePercentageThreshold = 10;
                        cb.PauseTime = 1.Minutes();
                    });
            }).StartAsync();
    }

    #endregion
}

public record ExpensiveReportRequest(Guid Id);
public record CheapNotification(Guid Id);
public record AccountLedgerEntry(Guid AccountId);

public static class DividingHandlersAcrossEndpoints
{
    #region sample_bp_dividing_handlers_across_endpoints

    public static async Task configure()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision();

                // Expensive work gets its own queue with capped parallelism so
                // it can never starve anything else
                opts.ListenToRabbitQueue("expensive")
                    .ProcessInline()
                    .ListenerCount(2);
                opts.PublishMessage<ExpensiveReportRequest>().ToRabbitQueue("expensive");

                // Cheap, high volume messages get a buffered endpoint with
                // plenty of parallelism
                opts.ListenToRabbitQueue("notifications")
                    .BufferedInMemory()
                    .MaximumParallelMessages(20);
                opts.PublishMessage<CheapNotification>().ToRabbitQueue("notifications");

                // When ordering matters more than throughput, process one at a
                // time on exactly one node in the cluster
                opts.ListenToRabbitQueue("ledger")
                    .ListenWithStrictOrdering();
                opts.PublishMessage<AccountLedgerEntry>().ToRabbitQueue("ledger");

                // The same knobs apply to local, in-process queues
                opts.LocalQueueFor<CheapNotification>()
                    .MaximumParallelMessages(8);
            }).StartAsync();
    }

    #endregion
}

#region sample_bp_sticky_handler

public record ProductUpdated(Guid ProductId);

// The same event is handled by two different handlers, and each handler
// gets its own queue. A failure or a slowdown in one never touches the other.
[StickyHandler("search-index")]
public static class ProductSearchIndexHandler
{
    public static void Handle(ProductUpdated message)
    {
        // update the search index
    }
}

[StickyHandler("pricing-cache")]
public static class ProductPricingCacheHandler
{
    public static void Handle(ProductUpdated message)
    {
        // refresh the pricing cache
    }
}

#endregion

public interface ITenantedService
{
    string TenantId { get; }
}

public class TenantedService(string tenantId) : ITenantedService
{
    public string TenantId { get; } = tenantId;
}

public static class IoCRegistrations
{
    #region sample_bp_avoid_runtime_bus_resolution

    public static void configure(IServiceCollection services)
    {
        // Don't do this. The IMessageBus resolved here is a brand new object
        // with no relationship to the message Wolverine is currently handling,
        // so TenantId (and everything else) will be wrong
        services.AddScoped<ITenantedService>(s =>
        {
            var bus = s.GetRequiredService<IMessageBus>();
            return new TenantedService(bus.TenantId ?? "*DEFAULT*");
        });
    }

    #endregion
}

#region sample_bp_scoped_service_mismatch

// Any service that gets IMessageContext (or Envelope) from an IServiceProvider
// it was handed is looking at a *different* context than the one Wolverine
// is using for the current message. The tenant id below is going to be wrong.
public class BadService(IServiceProvider serviceProvider)
{
    public void DoThings()
    {
        var context = serviceProvider.GetRequiredService<IMessageContext>();
        Console.WriteLine($"Current tenant id is {context.TenantId}");
    }
}

// Take the context directly as a dependency instead, and Wolverine hands
// you the real one
public class GoodService(IMessageContext context)
{
    public void DoThings()
    {
        Console.WriteLine($"Current tenant id is {context.TenantId}");
    }
}

#endregion

public record ReindexCatalog;

#region sample_bp_message_bus_as_method_argument

public static class ReindexCatalogHandler
{
    // Prefer cascading messages, but when you do need the bus, take it as a
    // method argument and Wolverine will hand you the one for the current message
    public static async Task HandleAsync(ReindexCatalog command, IMessageBus bus)
    {
        await bus.PublishAsync(new ProductUpdated(Guid.NewGuid()));
    }
}

#endregion

public class ShippableOrder
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
}

public interface IShippingGateway
{
    Task RequestShipmentAsync(Guid orderId, CancellationToken token);
}

#region sample_bp_fine_grained_handlers

public record PlaceOrder(Guid OrderId, Guid CustomerId, decimal Amount);
public record OrderPlaced(Guid OrderId);
public record NotifyShipper(Guid OrderId);

// Step 1: the database transaction. This commits quickly, and the call to
// the shipping service becomes a cascading message instead of a direct call
// inside this transaction
public static class PlaceOrderHandler
{
    public static (Insert<ShippableOrder>, OrderPlaced, NotifyShipper) Handle(PlaceOrder command)
    {
        var order = new ShippableOrder
        {
            Id = command.OrderId,
            CustomerId = command.CustomerId,
            Amount = command.Amount
        };

        return (Storage.Insert(order), new OrderPlaced(order.Id), new NotifyShipper(order.Id));
    }
}

// Step 2: the call to the external web service, in its own handler with its
// own retry loop. A flaky shipping API retries here on its own schedule,
// without re-running the database work and without holding the first
// transaction open while it waits
public static class NotifyShipperHandler
{
    public static void Configure(Wolverine.Runtime.Handlers.HandlerChain chain)
    {
        chain.OnException<HttpRequestException>()
            .RetryWithCooldown(1.Seconds(), 5.Seconds(), 30.Seconds())
            .WithFullJitter()
            .Then.MoveToErrorQueue();
    }

    public static Task Handle(NotifyShipper command, IShippingGateway gateway, CancellationToken token)
    {
        return gateway.RequestShipmentAsync(command.OrderId, token);
    }
}

#endregion
