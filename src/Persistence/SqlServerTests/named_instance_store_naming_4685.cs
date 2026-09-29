using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// GH-4685. A host with a SQL Server outbox would not start when the <c>Data Source</c> named an instance
/// (<c>db-host\MSSQL2017</c>): naming the store threw
/// <c>UriFormatException: Invalid URI: The hostname could not be parsed</c>.
///
/// <para>The fault was upstream — <c>DatabaseDescriptor.DatabaseUri()</c> percent-escaped the server name
/// into the URI HOST position, and a percent-escape is not legal there. Fixed in JasperFx/jasperfx#919,
/// shipped in JasperFx 2.76.1. GH-4471 is only what made it reachable: that is where
/// <c>SqlServerTenantedMessageStore</c> started naming tenant stores with <c>DatabaseUri()</c>.</para>
///
/// <para>These run offline. <c>Describe()</c> parses the connection string with
/// <c>SqlConnectionStringBuilder</c> and never opens a connection, which is exactly why the failure hit at
/// startup before anything could connect — and why this can be asserted without a named-instance server.
/// The point of keeping it on the Wolverine side is that the pin is what carries the fix: drop back below
/// 2.76.1 and these fail.</para>
/// </summary>
public class named_instance_store_naming_4685
{
    private static SqlServerMessageStore storeFor(string connectionString)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = connectionString,
            Role = MessageStoreRole.Tenant,
            SchemaName = "receiver"
        };

        return new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, []);
    }

    [Theory]
    [InlineData(@"Server=db-host\MSSQL2017;Database=orders;Integrated Security=true;")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=orders;Integrated Security=true;")]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=orders;Integrated Security=true;")]
    [InlineData(@"Server=tcp:db-host\MSSQL2017,1433;Database=orders;Integrated Security=true;")]
    public void a_named_instance_data_source_can_name_its_store(string connectionString)
    {
        // This is the reported line verbatim: SqlServerTenantedMessageStore.buildTenantStoreForConnectionString
        // sets store.Name = store.Describe().DatabaseUri().ToString(), and the throw came out of DatabaseUri().
        var store = storeFor(connectionString);

        Should.NotThrow(() => store.Describe().DatabaseUri().ToString());
    }

    [Fact]
    public void a_named_instance_store_is_still_named_distinctly_per_database()
    {
        // The reason GH-4471 set the name at all: MultiTenantedQueueListener and MultiTenantedQueueSender
        // both cache per tenant database BY NAME, and every store answering to the same name sent one
        // tenant's messages into another tenant's database. Sanitizing the host must not reintroduce that
        // by collapsing two instances onto one name.
        var first = storeFor(@"Server=db-host\MSSQL2017;Database=orders;Integrated Security=true;")
            .Describe().DatabaseUri().ToString();

        var second = storeFor(@"Server=db-host\MSSQL2019;Database=orders;Integrated Security=true;")
            .Describe().DatabaseUri().ToString();

        first.ShouldNotBe(second);
        first.ShouldNotBe("default");
    }

    [Fact]
    public void an_ordinary_host_name_is_unchanged()
    {
        // DatabaseUri is load-bearing as an identity (agent URIs, database ids), so the upstream fix had to
        // rename nothing that already worked. Pin that from this side too: a plain host must survive intact.
        storeFor("Server=db-host;Database=orders;Integrated Security=true;")
            .Describe().DatabaseUri().ShouldBe(new Uri("sqlserver://db-host/orders/receiver"));
    }
}
