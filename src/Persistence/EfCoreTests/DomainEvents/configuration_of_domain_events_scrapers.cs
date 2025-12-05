using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;
using CommandExtensions = Weasel.Core.CommandExtensions;

namespace EfCoreTests.DomainEvents;

[Collection("sqlserver")]
public class configuration_of_domain_events_scrapers : IAsyncDisposable
{
    private IHost theHost;

    public configuration_of_domain_events_scrapers()
    {
        ItemsTable = new Table(new DbObjectName("mt_items", "items"));
        ItemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        ItemsTable.AddColumn<string>("Name");
        ItemsTable.AddColumn<bool>("Approved");
    }

    public async Task startHostAsync(Action<WolverineOptions> configure)
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();


                configure(opts);
                //opts.PublishDomainEventsFromEntityFrameworkCore();
                
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        await withItemsTable();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
    
    public Table ItemsTable { get; }

    private async Task withItemsTable()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            var migration = await SchemaMigration.DetermineAsync(conn, ItemsTable);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                var sqlServerMigrator = new SqlServerMigrator();
                
                await sqlServerMigrator.ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
            }

            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task publish_domain_events_with_DomainEvents()
    {
        await startHostAsync(opts => opts.PublishDomainEventsFromEntityFrameworkCore());

        using var scope = theHost.Services.CreateAsyncScope();
        
        scope.ServiceProvider.GetRequiredService<Wolverine.EntityFrameworkCore.DomainEvents>().ShouldNotBeNull();

        var container = theHost.Services.GetRequiredService<IServiceContainer>();
        container.DefaultFor<Wolverine.EntityFrameworkCore.DomainEvents>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
        
        scope.ServiceProvider.GetServices<IDomainEventScraper>().Single().ShouldBeOfType<DomainEventsScraper>();
    }
}