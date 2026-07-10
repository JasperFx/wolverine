using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.EfCoreOnly;

// This little assembly exists so that Wolverine.Http.Tests can spin up hosts that use EF Core but NOT
// Marten (see Bug_3353_lightweight_storage_action_cascade). Pinning opts.ApplicationAssembly here keeps
// HTTP endpoint discovery away from the main test assembly, whose endpoints assume Marten is registered
// ([Entity]-loaded Marten documents, aggregate handlers, ...) and blow up chain construction without it.

public class Bug3353DbContext : DbContext
{
    public Bug3353DbContext(DbContextOptions<Bug3353DbContext> options) : base(options)
    {
    }

    public DbSet<Bug3353Item> Items => Set<Bug3353Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Bug3353Item>(map =>
        {
            map.ToTable("bug3353_items", "bug3353");
            map.HasKey(x => x.Id);
        });
    }
}

public class Bug3353Item
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record Bug3353CreateItem(string Name);

public record Bug3353ItemStored(Guid Id);

public static class Bug3353StorageActionEndpoint
{
    // Persists ONLY through the storage action, and never injects Bug3353DbContext. That combination
    // routes this chain to EFCorePersistenceFrameProvider.ApplyTransactionSupport(chain, container,
    // entityType) instead of the two-argument overload that carries the GH-3291 fix - CanApply is false
    // without a DbContext service dependency, so AutoApplyTransactions never claims the chain.
    [WolverinePost("/bug3353/storage-action")]
    public static (IResult, Insert<Bug3353Item>, Bug3353ItemStored) Post(Bug3353CreateItem command)
    {
        var item = new Bug3353Item { Id = Guid.NewGuid(), Name = command.Name };
        return (Results.Ok(), Storage.Insert(item), new Bug3353ItemStored(item.Id));
    }
}
