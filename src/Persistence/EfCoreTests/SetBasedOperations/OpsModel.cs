using Microsoft.EntityFrameworkCore;

namespace EfCoreTests.SetBasedOperations;

/// <summary>
///     Model used by the GH-4629 set-based operation tests. Deliberately in a file of its own so the
///     concurrent <c>[Entity]</c> work on GH-4635 and this branch never touch the same test model.
/// </summary>
public class OpsRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public bool Archived { get; set; }
    public int Tally { get; set; }
}

public class OpsDbContext : DbContext
{
    public OpsDbContext(DbContextOptions<OpsDbContext> options) : base(options)
    {
    }

    public DbSet<OpsRecord> Records { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpsRecord>(map =>
        {
            map.ToTable("ops_records");
            map.HasKey(x => x.Id);
            map.Property(x => x.Id).HasColumnName("id");
            map.Property(x => x.Name).HasColumnName("name");
            map.Property(x => x.Archived).HasColumnName("archived");
            map.Property(x => x.Tally).HasColumnName("tally");
        });
    }
}

/// <summary>
///     Thrown on purpose by the handlers below, so a test can tell "the handler failed the way the
///     test asked it to" apart from "the handler failed".
/// </summary>
public class DeliberateSetBasedFailure : Exception
{
    public DeliberateSetBasedFailure() : base("Deliberate failure after a set-based operation")
    {
    }
}
