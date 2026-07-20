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

    // GH-3538: a POST endpoint whose ONLY complex parameter is a DbContext. Before the fix the
    // body-inference decided the DbContext was the JSON request body, so posting with no body
    // 400'd with "Invalid JSON format". The DbContext must resolve as a service instead.
    [WolverinePost("/conjoined/notes/quick-add")]
    public static void QuickAdd(ConjoinedNotesDbContext db)
    {
        db.Notes.Add(new TenantedNote { Id = Guid.NewGuid(), Text = "quick-add" });
    }
}
