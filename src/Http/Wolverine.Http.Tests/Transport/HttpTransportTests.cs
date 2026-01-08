using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Wolverine.Runtime;
using NSubstitute;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class HttpTransportTests
{
    [Fact]
    public void protocol_is_https()
    {
        var transport = new HttpTransport();
        transport.Protocol.ShouldBe("https");
    }

    [Fact]
    public void can_get_endpoint_by_uri()
    {
        var transport = new HttpTransport();
        var uri = "https://localhost:5500".ToUri();
        var endpoint = transport.EndpointFor(uri.ToString());
        
        endpoint.Uri.ShouldBe(uri);
        endpoint.Role.ShouldBe(EndpointRole.Application);
    }
    
    [Fact]
    public void endpoints_are_cached()
    {
        var transport = new HttpTransport();
        var uri = "https://localhost:5500".ToUri();
        var endpoint1 = transport.EndpointFor(uri.ToString());
        var endpoint2 = transport.EndpointFor(uri.ToString());
        
        endpoint1.ShouldBeSameAs(endpoint2);
    }

    [Fact]
    public async Task initialize_compiles_all_endpoints()
    {
        var transport = new HttpTransport();
        var uri1 = "https://localhost:5501".ToUri();
        var uri2 = "https://localhost:5502".ToUri();
        
        var endpoint1 = transport.EndpointFor(uri1.ToString());
        var endpoint2 = transport.EndpointFor(uri2.ToString());
        
        var runtime = NSubstitute.Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        
        await transport.InitializeAsync(runtime);
    }
    
    [Fact]
    public void can_find_endpoint_by_uri()
    {
        var transport = new HttpTransport();
        var uri = "https://localhost:5503".ToUri();
        var endpoint = transport.EndpointFor(uri.ToString());
        
        var found = transport.TryGetEndpoint(uri);
        found.ShouldBeSameAs(endpoint);
    }
}
