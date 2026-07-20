using Microsoft.EntityFrameworkCore;

namespace ConjoinedMultiTenantedEfCore.Invoicing;

// A completely vanilla DbContext. Notice what's *not* here:
//
//   * no mapping for Invoice.TenantId
//   * no HasQueryFilter() anybody has to remember for every new entity
//   * no SaveChanges override stamping tenant ids
//   * no interceptors
//
// Wolverine's conjoined tenancy model customizer applies all of that
// automatically to every entity implementing ITenanted when this context is
// registered with AddDbContextWithWolverineManagedConjoinedTenancy<T>()
#region sample_conjoined_vanilla_dbcontext
public class InvoicingDbContext : DbContext
{
    public InvoicingDbContext(DbContextOptions<InvoicingDbContext> options) : base(options)
    {
    }

    public DbSet<Invoice> Invoices { get; set; } = null!;
    public DbSet<Product> Products { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(map =>
        {
            map.ToTable("invoices", "invoicing");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Product>(map =>
        {
            map.ToTable("products", "invoicing");
            map.HasKey(x => x.Id);
        });
    }
}
#endregion
