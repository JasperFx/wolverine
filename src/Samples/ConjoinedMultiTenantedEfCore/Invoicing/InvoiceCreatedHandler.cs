using Microsoft.Extensions.Logging;

namespace ConjoinedMultiTenantedEfCore.Invoicing;

// **5 (continued). Tenant-scoped queries through message handlers**
//
// This handler runs on a durable local queue, *outside* the HTTP request, and
// still has zero tenant plumbing. The InvoiceCreated message cascaded from the
// POST /invoices endpoint carries the tenant id on its envelope, so:
//
//   * the InvoicingDbContext injected here is pinned to that tenant
//   * FindAsync below can only ever see that tenant's invoice
//   * any write is stamped/guarded exactly like in the endpoint
//
// The transactional middleware saves and commits when the handler succeeds
#region sample_conjoined_tenant_scoped_handler
public static class InvoiceCreatedHandler
{
    // Toy business rule: small invoices are approved automatically
    public const decimal AutoApprovalLimit = 500;

    public static async Task Handle(InvoiceCreated message, InvoicingDbContext db, ILogger logger)
    {
        // Tenant-scoped load -- a message for tenant "acme" can never touch
        // an "initech" invoice, even though both live in the same table
        var invoice = await db.Invoices.FindAsync(message.InvoiceId);
        if (invoice == null)
        {
            return;
        }

        if (invoice.Amount <= AutoApprovalLimit)
        {
            invoice.Status = InvoiceStatus.Approved;
            logger.LogInformation("Auto-approved invoice {InvoiceId} for tenant {TenantId}",
                invoice.Id, invoice.TenantId);
        }
        else
        {
            logger.LogInformation("Invoice {InvoiceId} for tenant {TenantId} needs manual approval",
                invoice.Id, invoice.TenantId);
        }
    }
}
#endregion
