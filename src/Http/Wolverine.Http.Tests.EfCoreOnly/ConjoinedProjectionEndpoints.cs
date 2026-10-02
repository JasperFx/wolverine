using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.EfCoreOnly;

// [WolverineIgnore]d so other hosts scanning this assembly don't build chains for a DbContext they don't register.
// conjoined_projection_query_plan opts them back in.

public class ProjectionMember : ITenanted
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string? TenantId { get; set; }
}

public class ProjectionUser : ITenanted
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}

public class ConjoinedProjectionDbContext : DbContext
{
    public ConjoinedProjectionDbContext(DbContextOptions<ConjoinedProjectionDbContext> options) : base(options)
    {
    }

    public DbSet<ProjectionMember> Members => Set<ProjectionMember>();
    public DbSet<ProjectionUser> Users => Set<ProjectionUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectionMember>(map =>
        {
            map.ToTable("members", "conjoined_projection");
            map.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ProjectionUser>(map =>
        {
            map.ToTable("users", "conjoined_projection");
            map.HasKey(x => x.Id);
        });
    }
}

// Not an entity: no DbContext maps it
public class MemberWithUser
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class MemberWithUserById : QueryPlan<ConjoinedProjectionDbContext, MemberWithUser>
{
    private readonly Guid _id;

    public MemberWithUserById(Guid id)
    {
        _id = id;
    }

    public override IQueryable<MemberWithUser> Query(ConjoinedProjectionDbContext dbContext)
    {
        return from member in dbContext.Members
            join user in dbContext.Users on member.UserId equals user.Id
            where member.Id == _id
            select new MemberWithUser { Name = member.Name, Email = user.Email };
    }
}

public record CreateProjectionMember(Guid Id, string Name, string Email);

[WolverineIgnore]
public static class ConjoinedProjectionEndpoints
{
    [WolverinePost("/conjoined-projection/members")]
    public static void Create(CreateProjectionMember command, ConjoinedProjectionDbContext db)
    {
        var user = new ProjectionUser { Id = Guid.NewGuid(), Email = command.Email };
        db.Users.Add(user);
        db.Members.Add(new ProjectionMember { Id = command.Id, Name = command.Name, UserId = user.Id });
    }

    [WolverineGet("/conjoined-projection/members/{id}")]
    public static string Get([FromQuerySpecification(typeof(MemberWithUserById))] MemberWithUser? member)
    {
        return member == null ? "missing" : $"{member.Name} <{member.Email}>";
    }

    [NonTransactional]
    [WolverineGet("/conjoined-projection/members/{id}/non-transactional")]
    public static string GetNonTransactional(
        [FromQuerySpecification(typeof(MemberWithUserById))] MemberWithUser? member)
    {
        return member == null ? "missing" : $"{member.Name} <{member.Email}>";
    }
}
