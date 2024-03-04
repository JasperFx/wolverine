using IntegrationTests;
using Lamar;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;

namespace SqlServerTests;

public class extension_registrations : SqlServerContext
{
    [Fact]
    public void registrations()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));
        var container = runtime.Get<IContainer>();

        container.Model.HasRegistrationFor<IMessageStore>().ShouldBeTrue();
    }
}