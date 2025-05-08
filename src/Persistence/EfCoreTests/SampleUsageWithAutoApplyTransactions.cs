using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Wolverine;
using Wolverine.EntityFrameworkCore;

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

            opts.UseEntityFrameworkCoreTransactions();

            // Add the auto transaction middleware attachment policy
            opts.Policies.AutoApplyTransactions();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }
}