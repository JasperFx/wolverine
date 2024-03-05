using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_749_assert_on_schema_names_with_mixed_case
{
    [Fact]
    public async Task should_apply_transaction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                Should.Throw<ArgumentOutOfRangeException>(() =>
                {
                    services.AddMarten(Servers.PostgresConnectionString)
                        .IntegrateWithWolverine("SomethingMixed");
                });


            })
            .UseWolverine(opts => { opts.Policies.AutoApplyTransactions(); })
            .StartAsync();

    }
}