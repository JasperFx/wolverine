using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-5435. Two tenant databases with one name on one host but different ports used to share an agent Uri,
/// because the Uri leaves the port out. The store keeps that Uri, and only switches to one that carries the
/// port when <see cref="Wolverine.Persistence.MessageStoreCollection" /> finds the collision. These run offline:
/// building the store and its Uri never opens a connection
/// </summary>
public class agent_uri_carries_the_port_when_asked_5435
{
    private static PostgresqlMessageStore storeOn(int port)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Host = "localhost", Port = port, Database = "tenants"
        }.ConnectionString;

        var settings = new DatabaseSettings
        {
            ConnectionString = connectionString, Role = MessageStoreRole.Tenant, SchemaName = "wolverine"
        };

        return new PostgresqlMessageStore(settings, new DurabilitySettings(), NpgsqlDataSource.Create(connectionString),
            NullLogger<PostgresqlMessageStore>.Instance);
    }

    [Fact]
    public void the_uri_leaves_the_port_out_until_there_is_a_collision()
    {
        // Unchanged from before GH-5435, so no deployment's agent Uris move
        storeOn(5433).Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost/tenants/wolverine"));
        storeOn(5445).Uri.ShouldBe(storeOn(5433).Uri);
    }

    [Fact]
    public void a_colliding_store_is_told_apart_by_its_port()
    {
        var red = storeOn(5433);
        var blue = storeOn(5445);

        ((IPortQualifiedAgentUri)red).AgentUriPort.ShouldBe(5433);
        ((IPortQualifiedAgentUri)blue).AgentUriPort.ShouldBe(5445);

        ((IPortQualifiedAgentUri)red).UsePortQualifiedUri();
        ((IPortQualifiedAgentUri)blue).UsePortQualifiedUri();
        ((IPortQualifiedAgentUri)blue).UsePortQualifiedUri();

        red.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5433/tenants/wolverine"));
        blue.Uri.ShouldBe(new Uri("wolverinedb://postgresql/localhost:5445/tenants/wolverine"));
    }
}
