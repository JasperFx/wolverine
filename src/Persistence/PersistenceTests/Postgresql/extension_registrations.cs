using System.Data.Common;
using IntegrationTests;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PersistenceTests.Marten;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Xunit;

namespace PersistenceTests.Postgresql;

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