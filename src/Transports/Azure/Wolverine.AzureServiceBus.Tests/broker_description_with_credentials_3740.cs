using Azure.Core;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

// GH-3740: AzureServiceBusTransport.HostName dereferenced ConnectionString! and therefore threw a
// NullReferenceException for every credential-based connection (FullyQualifiedNamespace + a TokenCredential /
// NamedKeyCredential / SasCredential), where there is no connection string at all. Because HostName is a
// public getter, JasperFx's OptionsDescription reached it reflectively while building Wolverine's
// ServiceCapabilities snapshot -- so a stock managed-identity setup could never hand its capabilities to a
// monitoring console.
public class broker_description_with_credentials_3740
{
    [Fact]
    public void host_name_falls_back_to_the_fully_qualified_namespace()
    {
        var transport = new AzureServiceBusTransport
        {
            FullyQualifiedNamespace = "myns.servicebus.windows.net",
            TokenCredential = new StubTokenCredential()
        };

        transport.HostName.ShouldBe("myns.servicebus.windows.net");
    }

    [Fact]
    public void can_describe_a_broker_connected_with_a_token_credential()
    {
        var transport = new AzureServiceBusTransport
        {
            FullyQualifiedNamespace = "myns.servicebus.windows.net",
            TokenCredential = new StubTokenCredential()
        };

        var description = new BrokerDescription(transport);

        description.Endpoint.ShouldBe("myns.servicebus.windows.net");
        description.PropertyFor("HostName")!.Value.ShouldBe("myns.servicebus.windows.net");
    }

    [Fact]
    public void host_name_is_null_when_there_is_no_connection_information_at_all()
    {
        new AzureServiceBusTransport().HostName.ShouldBeNull();

        // and describing it is still harmless
        new BrokerDescription(new AzureServiceBusTransport()).Endpoint.ShouldBeNull();
    }

    [Fact]
    public void host_name_still_comes_from_the_connection_string_when_there_is_one()
    {
        var transport = new AzureServiceBusTransport
        {
            // The base64 SharedAccessKey has '=' padding -- the Endpoint parse has to stop at the first '='
            ConnectionString =
                "Endpoint=sb://fromconnectionstring.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=abc123==",
            FullyQualifiedNamespace = "ignored.servicebus.windows.net"
        };

        transport.HostName.ShouldBe("fromconnectionstring.servicebus.windows.net");
    }

    [Fact]
    public void host_name_falls_back_when_the_connection_string_has_no_endpoint_segment()
    {
        var transport = new AzureServiceBusTransport
        {
            ConnectionString = "SharedAccessKeyName=root;SharedAccessKey=abc123==",
            FullyQualifiedNamespace = "myns.servicebus.windows.net"
        };

        transport.HostName.ShouldBe("myns.servicebus.windows.net");
    }

    internal class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
