using System.Globalization;
using IntegrationTests;
using JasperFx.Core;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Transport;

namespace SqlServerTests.Transport;

public class SqlServerTransportTests
{
    private readonly SqlServerTransport theTransport = new SqlServerTransport(new DatabaseSettings
    {
        ConnectionString = Servers.SqlServerConnectionString,
        SchemaName = "transport"
    });

    [Fact]
    public void retrieve_queue_by_uri()
    {
        var queue = theTransport.GetOrCreateEndpoint("sqlserver://one".ToUri());
        queue.ShouldBeOfType<SqlServerQueue>().Name.ShouldBe("one");
    }

    // A queue reached only by Uri (ListenForMessagesFrom("sqlserver://my-service-control"))
    // bypasses the fluent API's MaybeCorrectName, and the raw dashed name reached the
    // wolverine_queue_* table DDL as an invalid, unbracketed identifier.
    [Fact]
    public void queue_name_from_uri_is_sanitized()
    {
        var queue = theTransport.GetOrCreateEndpoint("sqlserver://my-service-control".ToUri());
        queue.ShouldBeOfType<SqlServerQueue>().Name.ShouldBe("my_service_control");
    }

    [Fact]
    public void dashed_uri_and_fluent_name_resolve_to_the_same_endpoint()
    {
        var queue = theTransport.Queues[theTransport.MaybeCorrectName("my-service-control")];
        theTransport.GetOrCreateEndpoint("sqlserver://my-service-control".ToUri()).ShouldBeSameAs(queue);
        theTransport.GetOrCreateEndpoint(queue.Uri).ShouldBeSameAs(queue);
        theTransport.Queues.Count().ShouldBe(1);
    }

    // Regression test for https://github.com/JasperFx/wolverine/issues/2472
    // Turkish culture maps 'I'.ToLower() to dotless 'ı' instead of 'i',
    // which corrupts SQL identifiers when SanitizeIdentifier uses ToLower().
    [Fact]
    public void sanitize_identifier_is_culture_invariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            // "INCOMING" contains 'I' — Turkish ToLower() would produce "ıncomıng"
            theTransport.SanitizeIdentifier("INCOMING").ShouldBe("incoming");
            theTransport.SanitizeIdentifier("My-Queue-Name").ShouldBe("my_queue_name");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}