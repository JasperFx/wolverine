using JasperFx.Descriptors;
using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

// DatabaseServerId is now built from a DatabaseDescriptor (jasperfx#514 added Port to the descriptor),
// so a provider populates Engine / ServerName / Port once in Describe() and the connection-budget key
// and the diagnostic descriptor can't disagree.
public class database_server_id_factory_tests
{
    [Fact]
    public void maps_engine_server_and_port_from_the_descriptor()
    {
        var descriptor = new DatabaseDescriptor
        {
            Engine = "PostgreSQL",
            ServerName = "shard-a",
            Port = 5433
        };

        var id = DatabaseServerId.For(descriptor);

        id.Engine.ShouldBe("PostgreSQL");
        id.ServerName.ShouldBe("shard-a");
        id.Port.ShouldBe(5433);
    }

    [Fact]
    public void carries_a_null_port_when_the_descriptor_leaves_it_null()
    {
        // SqlServer folds the port into the server name, so its descriptor leaves Port null.
        var descriptor = new DatabaseDescriptor
        {
            Engine = "SqlServer",
            ServerName = @"localhost,1433",
            Port = null
        };

        DatabaseServerId.For(descriptor).Port.ShouldBeNull();
    }
}
