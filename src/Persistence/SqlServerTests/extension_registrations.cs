using IntegrationTests;
using JasperFx;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;

namespace SqlServerTests;

public class extension_registrations : SqlServerContext
{
    [Fact]
    public async Task registrations()
    {
        using var runtime = await WolverineHost.ForAsync(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));
        var container = runtime.Get<IServiceContainer>();

        container.HasRegistrationFor<IMessageStore>().ShouldBeTrue();
    }
}