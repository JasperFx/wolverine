using System.Data.Common;
using IntegrationTests;
using Lamar;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Xunit;

namespace PersistenceTests.SqlServer;

public class extension_registrations : SqlServerContext
{
    [Fact]
    public void registrations()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));
        var container = runtime.Get<IContainer>();

        container.Model.HasRegistrationFor<SqlConnection>().ShouldBeTrue();
        container.Model.HasRegistrationFor<DbConnection>().ShouldBeTrue();

        container.Model.For<SqlConnection>().Default.Lifetime.ShouldBe(ServiceLifetime.Scoped);


        container.Model.HasRegistrationFor<IMessageStore>().ShouldBeTrue();


        runtime.Get<SqlConnection>().ConnectionString.ShouldBe(Servers.SqlServerConnectionString);
        runtime.Get<DbConnection>().ShouldBeOfType<SqlConnection>()
            .ConnectionString.ShouldBe(Servers.SqlServerConnectionString);
    }
}