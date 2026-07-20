using ConjoinedMultiTenantedEfCore.Invoicing;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

namespace ConjoinedMultiTenantedEfCore.Demos;

public record HijackInvoice(Guid InvoiceId, string NewDescription);

public record CrossTenantWriteAttempted(
    bool Rejected,
    string Explanation,
    string? EntityTenantId = null,
    string? ContextTenantId = null);

// **4. Cross-tenant write rejection**
//
// The tenant-bound query filter already makes it hard to *reach* another
// tenant's rows, but a determined (or just buggy) piece of code can always
// smuggle one out with IgnoreQueryFilters(). This endpoint does exactly that on
// purpose to show the second line of defense: Wolverine's stamping interceptor
// inspects every modified/deleted ITenanted entity at SaveChanges time and
// refuses to flush a row that belongs to a different tenant, throwing
// CrossTenantWriteException before anything hits the database.
//
// Try it: create an invoice as tenant "acme", then call this endpoint with the
// invoice id as tenant "initech"
#region sample_conjoined_cross_tenant_write_rejection
public static class CrossTenantWriteDemo
{
    [WolverinePost("/demos/cross-tenant-write")]
    public static async Task<CrossTenantWriteAttempted> Attempt(HijackInvoice command, InvoicingDbContext db)
    {
        // IgnoreQueryFilters() is the "one forgotten filter" from the motivating
        // blog post, weaponized: it lets us see (and track) rows from every tenant
        var smuggled = await db.Invoices.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.Id == command.InvoiceId);
        if (smuggled == null)
        {
            return new CrossTenantWriteAttempted(false,
                $"No invoice with id {command.InvoiceId} exists for any tenant");
        }

        smuggled.Description = command.NewDescription;

        try
        {
            await db.SaveChangesAsync();

            // Only reachable when the invoice already belongs to the calling tenant
            return new CrossTenantWriteAttempted(false,
                "The write succeeded because the invoice belongs to the calling tenant. " +
                "Call this endpoint again with a different tenant-id header to see the rejection.");
        }
        catch (CrossTenantWriteException e)
        {
            // Nothing was written. Clear the poisoned change tracker so the
            // transactional middleware's own SaveChangesAsync stays a no-op
            db.ChangeTracker.Clear();

            return new CrossTenantWriteAttempted(true, e.Message, e.EntityTenantId, e.ContextTenantId);
        }
    }
}
#endregion
