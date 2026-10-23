using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;

namespace MySqlTests;

[Collection("mysql")]
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
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "dlq5");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        await host.Services.GetRequiredService<IMessageStore>().Admin.ClearAllAsync();

        return host;
    }
}
