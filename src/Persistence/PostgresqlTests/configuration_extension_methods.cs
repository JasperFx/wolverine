using IntegrationTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class configuration_extension_methods : PostgresqlContext
{
    [Fact]
    public void bootstrap_with_connection_string()
    {
        using var host = WolverineHost.For(x =>
            x.PersistMessagesWithPostgresql(Servers.PostgresConnectionString));
        host.Services.GetRequiredService<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.PostgresConnectionString);

    }
}