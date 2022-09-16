using System.Data.Common;
using IntegrationTests;
using Lamar;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using TestingSupport;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Postgresql;
using Wolverine.Persistence.Testing.Marten;
using Xunit;

namespace Wolverine.Persistence.Testing.Postgresql;

public class extension_registrations : PostgresqlContext
{
    [Fact]
    public void registrations()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithPostgresql(Servers.PostgresConnectionString));

        var container = runtime.Get<IContainer>();
        container.Model.HasRegistrationFor<NpgsqlConnection>().ShouldBeTrue();
        container.Model.HasRegistrationFor<DbConnection>().ShouldBeTrue();

        container.Model.For<NpgsqlConnection>().Default.Lifetime.ShouldBe(ServiceLifetime.Scoped);


        container.Model.HasRegistrationFor<IEnvelopePersistence>().ShouldBeTrue();


        runtime.Get<NpgsqlConnection>().ConnectionString.ShouldBe(Servers.PostgresConnectionString);
        runtime.Get<DbConnection>().ShouldBeOfType<NpgsqlConnection>()
            .ConnectionString.ShouldBe(Servers.PostgresConnectionString);
    }
}
