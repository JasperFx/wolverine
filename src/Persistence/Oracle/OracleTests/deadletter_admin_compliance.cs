using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;

namespace OracleTests;

[Collection("oracle")]
public class deadletter_admin_compliance : DeadLetterAdminCompliance
{
    public deadletter_admin_compliance(ITestOutputHelper output) : base(output)
    {
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        await host.Services.GetRequiredService<IMessageStore>().Admin.ClearAllAsync();

        return host;
    }
}
