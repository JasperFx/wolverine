using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Xunit;

namespace PostgresqlTests;

// Consolidation check (jasperfx#514 / connection-budget follow-up): the PostgreSQL store's ServerId is
// now sourced from Describe(), so the connection-budget key and the diagnostic descriptor agree on the
// host and port. Wolverine's docker-compose PostgreSQL runs on 5433, which is exactly the collision case
// the port is in the key for.
public class database_server_id_matches_descriptor : PostgresqlContext
{
    [Fact]
    public void server_id_carries_the_port_and_matches_the_descriptor()
    {
        var settings = new DatabaseSettings { ConnectionString = Servers.PostgresConnectionString };
        var store = new PostgresqlMessageStore(settings, new DurabilitySettings(),
            NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        var expectedPort = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString).Port;

        var descriptor = store.Describe();
        descriptor.Port.ShouldBe(expectedPort);

        var serverId = store.ServerId;
        serverId.Engine.ShouldBe("PostgreSQL");
        serverId.Port.ShouldBe(expectedPort);
        serverId.ServerName.ShouldBe(descriptor.ServerName);

        // The whole point of the consolidation: one source of truth.
        serverId.Port.ShouldBe(descriptor.Port);
        serverId.ServerName.ShouldBe(descriptor.ServerName);
    }
}
