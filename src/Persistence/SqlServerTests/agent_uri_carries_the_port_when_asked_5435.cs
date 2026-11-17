using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// GH-5435. SQL Server spells the port into the server name (<c>host,port</c>), and the agent Uri splits it off.
/// A colliding tenant store gets it back when <see cref="Wolverine.Persistence.MessageStoreCollection" /> asks.
/// Offline, like <see cref="named_instance_store_naming_4685" />
/// </summary>
public class agent_uri_carries_the_port_when_asked_5435
{
    private static SqlServerMessageStore storeFor(string server)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = $"Server={server};Database=tenants;User Id=sa;Password=x;TrustServerCertificate=true",
            Role = MessageStoreRole.Tenant,
            SchemaName = "wolverine"
        };

        return new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, []);
    }

    [Fact]
    public void the_port_after_the_comma_tells_two_stores_apart()
    {
        var red = storeFor("localhost,1433");
        var blue = storeFor("localhost,1434");

        red.Uri.ShouldBe(blue.Uri);
        ((IPortQualifiedAgentUri)blue).AgentUriPort.ShouldBe(1434);

        ((IPortQualifiedAgentUri)red).UsePortQualifiedUri();
        ((IPortQualifiedAgentUri)blue).UsePortQualifiedUri();

        red.Uri.ShouldNotBe(blue.Uri);
        blue.Uri.ToString().ShouldContain("localhost:1434");
    }

    [Fact]
    public void a_server_without_a_port_has_nothing_to_tell_it_apart()
    {
        var store = storeFor("db-host");
        var before = store.Uri;

        ((IPortQualifiedAgentUri)store).AgentUriPort.ShouldBeNull();
        ((IPortQualifiedAgentUri)store).UsePortQualifiedUri();

        store.Uri.ShouldBe(before);
    }
}
