using System.ComponentModel.DataAnnotations.Schema;
using IntegrationTests;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Environment;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals.Migrations;
using Wolverine.SqlServer;

namespace EfCoreTests.Migrations;

[Collection("sqlserver")]
public class with_one_sqlserver_context : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("blogs");
        await conn.CloseAsync();
        
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<BloggingContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.Discovery.DisableConventionalDiscovery();
                
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "blogs");
                opts.UseEntityFrameworkCoreTransactions();
                
                // TODO -- this might go away and get merged into UseEntityFrameworkCoreTransactions() above
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        SqlConnection.ClearAllPools();
    }

    [Fact]
    public void registers_the_system_part()
    {
        _host.Services.GetServices<ISystemPart>().OfType<EntityFrameworkCoreSystemPart>()
            .Any().ShouldBeTrue();
    }

    [Fact]
    public async Task did_apply()
    {
        using var scope = _host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BloggingContext>();

        await context.Blogs.AddAsync(new Blog()
        {
            BlogId = 1,
            Url = "http://codebetter.com"
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task smoke_test_write_to_console()
    {
        var part = _host.Services.GetServices<ISystemPart>().OfType<EntityFrameworkCoreSystemPart>().First();
        await part.WriteToConsole();
    }

    [Fact]
    public async Task smoke_test_check_connectivity()
    {
        var part = _host.Services.GetServices<ISystemPart>().OfType<EntityFrameworkCoreSystemPart>().First();
        var results = new EnvironmentCheckResults();
        await part.AssertEnvironmentAsync(_host.Services, results, CancellationToken.None);
        
        results.Failures.Any().ShouldBeFalse();
    }

    [Fact]
    public async Task smoke_tests_describe_databases()
    {
        var part = _host.Services.GetServices<ISystemPart>().OfType<EntityFrameworkCoreSystemPart>().First();
        var usage = await part.As<IDatabaseSource>().DescribeDatabasesAsync(CancellationToken.None);
        usage.ShouldNotBeNull();
    }
}

public class Blog
{
    [Column("id")]
    public int BlogId { get; set; }
    
    [Column("url")]
    public string Url { get; set; }
    // Navigation property for related posts
    public List<Post> Posts { get; set; }
}

public class Post
{
    [Column("post_id")]
    public int PostId { get; set; }
    
    [Column("title")]
    public string Title { get; set; }
    
    [Column("content")]
    public string Content { get; set; }
    // Foreign key to the Blog
    [Column("blog_id")]
    public int BlogId { get; set; }
    // Navigation property for the related blog
    public Blog Blog { get; set; }
}

public class BloggingContext : DbContext
{
    // DbSet properties represent the collections of entities in the context
    public DbSet<Blog> Blogs { get; set; }
    public DbSet<Post> Posts { get; set; }

    // Constructor for dependency injection (recommended in ASP.NET Core apps)
    public BloggingContext(DbContextOptions<BloggingContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("blogs");
        
  
    }
}