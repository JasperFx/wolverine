using IntegrationTests;
using Lamar;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PersistenceTests;

public class extension_registrations : PostgresqlContext
{
    [Fact]
    public void registrations()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithPostgresql(Servers.PostgresConnectionString));

        var container = runtime.Get<IContainer>();

        container.Model.HasRegistrationFor<IMessageStore>().ShouldBeTrue();
    }
}