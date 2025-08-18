using IntegrationTests;
using JasperFx;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace PostgresqlTests;

public class extension_registrations : PostgresqlContext
{
    [Fact]
    public void registrations()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithPostgresql(Servers.PostgresConnectionString));

        var container = runtime.Get<IServiceContainer>();

        container.HasRegistrationFor<IMessageStore>().ShouldBeTrue();
    }
}