using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace PostgresqlTests;

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
                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "dlq5";
                }).IntegrateWithWolverine();

                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();

        return host;
    }
}