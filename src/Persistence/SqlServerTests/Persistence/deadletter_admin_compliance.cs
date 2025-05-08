using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer;
using Xunit.Abstractions;

namespace SqlServerTests.Persistence;

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
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dlq5");
                
                // This setting changes the internal message storage identity
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
            })
            .StartAsync();

        var persistence = (IMessageDatabase)host.Services.GetRequiredService<IMessageStore>();
        await persistence.Admin.ClearAllAsync();

        return host;
    }
}