using IntegrationTests;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Environment;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals.Migrations;
using Wolverine.Postgresql;
using Wolverine.SqlServer;

namespace EfCoreTests.Migrations;

[Collection("postgresql")]
public class with_one_postgresql_context : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("blogs");
        await conn.CloseAsync();
        
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                WolverineEntityCoreExtensions.AddDbContextWithWolverineIntegration<BloggingContext>(opts.Services, x =>
                {
                    x.UseNpgsql(Servers.PostgresConnectionString);
                });

                opts.Discovery.DisableConventionalDiscovery();
                
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                opts.UseEntityFrameworkCoreTransactions();
                
                // TODO -- this might go away and get merged into UseEntityFrameworkCoreTransactions() above
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        NpgsqlConnection.ClearAllPools();
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