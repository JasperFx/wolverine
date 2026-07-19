using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Wolverine.Http.Tests.EfCoreOnly;

public class TenantedNote : ITenanted
{
    public Guid Id { get; set; }
    public string Text { get; set; } = null!;
    public string? TenantId { get; set; }
}

public class ConjoinedNotesDbContext : DbContext
{
    public ConjoinedNotesDbContext(DbContextOptions<ConjoinedNotesDbContext> options) : base(options)
    {
    }

    public DbSet<TenantedNote> Notes { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantedNote>(map =>
        {
            map.ToTable("tenanted_notes", "conjoined_http");
            map.HasKey(x => x.Id);
        });
    }
}

public record CreateNote(Guid Id, string Text);

public static class ConjoinedNotesEndpoint
{
    [WolverinePost("/conjoined/notes/create")]
    public static void Post(CreateNote command, ConjoinedNotesDbContext db)
    {
        db.Notes.Add(new TenantedNote { Id = command.Id, Text = command.Text });
    }

    [WolverineGet("/conjoined/notes")]
    public static Task<TenantedNote[]> Get(ConjoinedNotesDbContext db)
    {
        return db.Notes.OrderBy(x => x.Text).ToArrayAsync();
    }
}
