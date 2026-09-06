using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Transports;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-4200. The HTTP transport declares the scheme "https", and TransportCollection keys every transport
// by exactly one scheme, so `ToHttpEndpoint("http://some-api:80/")` built the endpoint happily and then
// died registering the subscription with "Unknown Transport scheme 'http'". A plain http:// destination
// is ordinary — services talking over a container network, a sidecar on localhost — and no other
// transport owns that scheme.
//
// The transport's existing ["http"] constructor argument looks like it addresses this and does not: it
// is the TAGS parameter, which has nothing to do with scheme resolution.
public class insecure_http_uri_4200
{
    [Fact]
    public void the_transport_answers_to_http_as_well_as_https()
    {
        var transport = new HttpTransport();

        transport.Protocol.ShouldBe("https");
        transport.AdditionalProtocols.ShouldContain("http");
    }

    [Fact]
    public void get_or_create_endpoint_accepts_an_insecure_uri()
    {
        ITransport transport = new HttpTransport();

        // The scheme guard on TransportBase rejected anything but the declared Protocol
        var endpoint = transport.GetOrCreateEndpoint("http://some-api:80/".ToUri());

        endpoint.Uri.Scheme.ShouldBe("http");
    }

    [Fact]
    public void the_scheme_guard_still_rejects_an_unrelated_scheme()
    {
        ITransport transport = new HttpTransport();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            transport.GetOrCreateEndpoint("ftp://some-api:80/".ToUri()));
    }

    [Fact]
    public void transport_collection_resolves_the_insecure_scheme()
    {
        var options = new WolverineOptions();
        options.Transports.GetOrCreate<HttpTransport>();

        options.Transports.ForScheme("http").ShouldBeOfType<HttpTransport>();
        options.Transports.ForScheme("https").ShouldBeOfType<HttpTransport>();
    }

    [Fact]
    public void an_aliased_transport_is_still_enumerated_exactly_once()
    {
        // The alias is resolved by a fallback scan rather than a second dictionary key precisely so that
        // the transport is not double-initialized and its endpoints not double-counted.
        var options = new WolverineOptions();
        options.Transports.GetOrCreate<HttpTransport>();

        options.Transports.OfType<HttpTransport>().Count().ShouldBe(1);
    }

    [Fact]
    public void an_unknown_scheme_still_fails()
    {
        var options = new WolverineOptions();
        options.Transports.ForScheme("nonsense").ShouldBeNull();
    }

    // The reporter's actual line. Subscribing is what threw, not building the endpoint.
    [Fact]
    public async Task publish_to_an_insecure_http_endpoint()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToHttpEndpoint("http://some-api:80/");
            }).StartAsync(TestContext.Current.CancellationToken);

        var endpoint = host.GetRuntime().Options.Transports
            .OfType<HttpTransport>()
            .Single()
            .Endpoints()
            .Single();

        endpoint.Uri.ShouldBe("http://some-api:80/".ToUri());
    }
}
