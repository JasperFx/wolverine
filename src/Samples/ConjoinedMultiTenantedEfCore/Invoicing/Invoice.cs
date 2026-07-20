using JasperFx.MultiTenancy;

namespace ConjoinedMultiTenantedEfCore.Invoicing;

public enum InvoiceStatus
{
    Pending,
    Approved
}

// **1. An ITenanted entity**
//
// Implementing JasperFx.MultiTenancy.ITenanted -- the very same marker interface
// that Marten uses for its conjoined tenancy -- is the *entire* opt-in for
// Wolverine's conjoined EF Core multi-tenancy. At bootstrapping time Wolverine:
//
//   * maps TenantId to a `tenant_id` column (with an index)
//   * adds a global query filter binding every query to the current tenant,
//     so nobody has to remember to add `.Where(x => x.TenantId == ...)` --
//     "one forgotten filter and Party A is reading Party B's mail" can't happen
//   * stamps TenantId with the ambient tenant id on insert
//   * rejects any cross-tenant update or delete with CrossTenantWriteException
//
// Note that this class has zero tenancy logic of its own, and neither does the
// DbContext mapping below. TenantId is framework-managed -- application code
// should never write to it.
#region sample_conjoined_invoice_entity
public class Invoice : ITenanted
{
    public Guid Id { get; set; }
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Wolverine maps, stamps, and hydrates this for you. Treat the
    // value as framework-managed
    public string? TenantId { get; set; }
}

// Deliberately NOT ITenanted. Entities that don't implement the marker are left
// completely alone -- no tenant_id column, no query filter, no guard. Perfect
// for reference data shared by every tenant (think a common product catalog)
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal ListPrice { get; set; }
}
#endregion
