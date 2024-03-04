using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests;

public class SampleUsageWithAutoApplyTransactions
{
    public static async Task bootstrap()
    {
        #region sample_bootstrapping_with_auto_apply_transactions_for_sql_server

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                var connectionString = context.Configuration.GetConnectionString("database");

                opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
                {
                    x.UseSqlServer(connectionString);
                });

                opts.UseEntityFrameworkCoreTransactions();

                // Add the auto transaction middleware attachment policy
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        #endregion
    }
}