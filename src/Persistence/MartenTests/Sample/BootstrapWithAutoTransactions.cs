using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.Sample;

public class BootstrapWithAutoTransactions
{
    public static async Task bootstrap()
    {
        #region sample_using_auto_apply_transactions_with_marten

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten("some connection string")
                    .IntegrateWithWolverine();

                // Opt into using "auto" transaction middleware
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        #endregion
    }
}