using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace ConjoinedMultiTenantedEfCore.Invoicing;

public record CreateInvoice(string Description, decimal Amount);

public record InvoiceCreated(Guid InvoiceId, decimal Amount);

public static class InvoiceEndpoints
{
    // **3. Stamp-on-insert**
    //
    // This endpoint has ZERO tenant awareness -- it never reads a header, never
    // touches Invoice.TenantId, and never calls SaveChangesAsync():
    //
    //   * Wolverine.Http detects the tenant from the request (see
    //     MapWolverineEndpoints in Program.cs) and hands this endpoint an
    //     InvoicingDbContext already pinned to that tenant
    //   * the tenant stamping interceptor writes the tenant id into the new
    //     row on insert
    //   * the EF Core transactional middleware (Policies.AutoApplyTransactions)
    //     calls SaveChangesAsync and commits the outgoing InvoiceCreated
    //     message through the durable outbox in the same transaction
    //
    // The second tuple value is a cascaded message. It's published only after
    // the transaction commits, and it *carries the tenant id with it*, so the
    // message handler below is tenant-scoped too
    [WolverinePost("/invoices")]
    public static (CreationResponse<InvoiceCreated>, InvoiceCreated) Create(
        CreateInvoice command,
        InvoicingDbContext db)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            Description = command.Description,
            Amount = command.Amount
        };

        db.Invoices.Add(invoice);

        var created = new InvoiceCreated(invoice.Id, invoice.Amount);
        return (CreationResponse.For(created, $"/invoices/{invoice.Id}"), created);
    }

    // **5. Tenant-scoped queries through HTTP endpoints**
    //
    // No Where(x => x.TenantId == ...) in sight. The global query filter that
    // Wolverine added to every ITenanted entity binds this query to the tenant
    // detected from the request. Call it as tenant "acme" and you only ever see
    // acme's invoices
    [WolverineGet("/invoices")]
    public static Task<Invoice[]> GetAll(InvoicingDbContext db)
    {
        return db.Invoices.OrderBy(x => x.CreatedAt).ToArrayAsync();
    }

    // FindAsync respects the tenant filter as well -- asking for another
    // tenant's invoice id returns null, which Wolverine.Http turns into a 404
    [WolverineGet("/invoices/{id}")]
    public static Task<Invoice?> GetById(Guid id, InvoicingDbContext db)
    {
        return db.Invoices.FindAsync(id).AsTask();
    }
}
