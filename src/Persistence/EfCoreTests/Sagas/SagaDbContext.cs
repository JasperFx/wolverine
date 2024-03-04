using Microsoft.EntityFrameworkCore;
using TestingSupport.Sagas;

namespace EfCoreTests.Sagas;

public class SagaDbContext : DbContext
{
    private readonly DbContextOptions _options;

    public SagaDbContext(DbContextOptions<SagaDbContext> options) : base(options)
    {
        _options = options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuidBasicWorkflow>(map =>
        {
            map.ToTable("GuidWorkflowState");
            map.HasKey(x => x.Id);
            map.Property(x => x.OneCompleted).HasColumnName("one");
            map.Property(x => x.TwoCompleted).HasColumnName("two");
            map.Property(x => x.ThreeCompleted).HasColumnName("three");
            map.Property(x => x.FourCompleted).HasColumnName("four");
        });

        modelBuilder.Entity<IntBasicWorkflow>(map =>
        {
            map.ToTable("IntWorkflowState");
            map.HasKey(x => x.Id);
            map.Property(x => x.OneCompleted).HasColumnName("one");
            map.Property(x => x.TwoCompleted).HasColumnName("two");
            map.Property(x => x.ThreeCompleted).HasColumnName("three");
            map.Property(x => x.FourCompleted).HasColumnName("four");
        });

        modelBuilder.Entity<StringBasicWorkflow>(map =>
        {
            map.ToTable("StringWorkflowState");
            map.HasKey(x => x.Id);
            map.Property(x => x.OneCompleted).HasColumnName("one");
            map.Property(x => x.TwoCompleted).HasColumnName("two");
            map.Property(x => x.ThreeCompleted).HasColumnName("three");
            map.Property(x => x.FourCompleted).HasColumnName("four");
        });

        modelBuilder.Entity<LongBasicWorkflow>(map =>
        {
            map.ToTable("LongWorkflowState");
            map.HasKey(x => x.Id);
            map.Property(x => x.OneCompleted).HasColumnName("one");
            map.Property(x => x.TwoCompleted).HasColumnName("two");
            map.Property(x => x.ThreeCompleted).HasColumnName("three");
            map.Property(x => x.FourCompleted).HasColumnName("four");
        });
    }
}