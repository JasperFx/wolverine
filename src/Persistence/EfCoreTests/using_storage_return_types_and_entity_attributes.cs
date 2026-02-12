using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests;

public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(x =>
        {
            x.UseSqlServer(Servers.SqlServerConnectionString);
        }, "wolverine");
        
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
        
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }

    public override async Task<Todo?> Load(string id)
    {
        using var scope = Host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        return await context.Todos.FindAsync(id);
    }

    public override async Task Persist(Todo todo)
    {
        using var scope = Host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        context.Todos.Add(todo);
        await context.SaveChangesAsync();
    }
}

#region sample_TodoDbContext

public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options)
    {
    }

    public DbSet<Todo> Todos { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Todo>(map =>
        {
            map.ToTable("todos", "todo_app");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Property(x => x.IsComplete).HasColumnName("is_complete");
        });
    }
}

#endregion