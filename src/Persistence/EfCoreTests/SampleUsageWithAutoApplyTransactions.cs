using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests;

public class SampleUsageWithAutoApplyTransactions
{
    public static async Task bootstrap()
    {
        #region sample_bootstrapping_with_auto_apply_transactions_for_sql_server

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var connectionString = builder.Configuration.GetConnectionString("database");

            opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
            {
                x.UseSqlServer(connectionString);
            });

            // Add the auto transaction middleware attachment policy
            opts.Policies.AutoApplyTransactions();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public static async Task quickstart()
    {
        #region sample_getting_started_with_efcore

        var builder = Host.CreateApplicationBuilder();

        var connectionString = builder.Configuration.GetConnectionString("sqlserver");

        // Register a DbContext or multiple DbContext types as normal
        builder.Services.AddDbContext<SampleDbContext>(
            x => x.UseSqlServer(connectionString), 
            
            // This is actually a significant performance gain
            // for Wolverine's sake
            optionsLifetime:ServiceLifetime.Singleton);

        // Register Wolverine
        builder.UseWolverine(opts =>
        {
            // You'll need to independently tell Wolverine where and how to 
            // store messages as part of the transactional inbox/outbox
            opts.PersistMessagesWithSqlServer(connectionString);
            
            // Adding EF Core transactional middleware, saga support,
            // and EF Core support for Wolverine storage operations
            opts.UseEntityFrameworkCoreTransactions();
        });
        
        // Rest of your bootstrapping...

        #endregion
    }

    public static async Task quickstart2()
    {

        #region sample_idiomatic_wolverine_registration_of_ef_core

        var builder = Host.CreateApplicationBuilder();

        var connectionString = builder.Configuration.GetConnectionString("sqlserver");
        
        builder.UseWolverine(opts =>
        {
            // You'll need to independently tell Wolverine where and how to 
            // store messages as part of the transactional inbox/outbox
            opts.PersistMessagesWithSqlServer(connectionString);
            
            // Registers the DbContext type in your IoC container, sets the DbContextOptions
            // lifetime to "Singleton" to optimize Wolverine usage, and also makes sure that
            // your Wolverine service has all the EF Core transactional middleware, saga support,
            // and storage operation helpers activated for this application
            opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(
                x => x.UseSqlServer(connectionString));
        });

        #endregion
    }
}