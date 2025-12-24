using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class HttpEndpointTests
{
    private readonly Uri _uri = "http://localhost:5000".ToUri();
    private readonly HttpEndpoint _endpoint;

    public HttpEndpointTests()
    {
        _endpoint = new HttpEndpoint(_uri, EndpointRole.Application);
    }

    private ISender InvokeCreateSender(IWolverineRuntime runtime)
    {
        var method = typeof(HttpEndpoint).GetMethod("CreateSender", BindingFlags.Instance | BindingFlags.NonPublic);
        return (ISender)method.Invoke(_endpoint, new object[] { runtime });
    }

    private bool InvokeSupportsMode(EndpointMode mode)
    {
        var method = typeof(HttpEndpoint).GetMethod("supportsMode", BindingFlags.Instance | BindingFlags.NonPublic);
        return (bool)method.Invoke(_endpoint, new object[] { mode });
    }

    [Fact]
    public void constructor_sets_uri_and_role()
    {
        _endpoint.Uri.ShouldBe(_uri);
        _endpoint.Role.ShouldBe(EndpointRole.Application);
    }

    [Fact]
    public async Task build_listener_returns_nullo_listener()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        var receiver = Substitute.For<IReceiver>();

        var listener = await _endpoint.BuildListenerAsync(runtime, receiver);

        listener.ShouldBeOfType<NulloListener>();
        listener.Address.ShouldBe(_uri);
    }

    [Fact]
    public void create_sender_inline_mode()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Services.Returns(Substitute.For<IServiceProvider>());
        
        _endpoint.Mode = EndpointMode.Inline;

        var sender = InvokeCreateSender(runtime);

        sender.ShouldBeOfType<InlineHttpSender>();
    }

    [Fact]
    public void create_sender_buffered_mode()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Services.Returns(Substitute.For<IServiceProvider>());
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        
        _endpoint.Mode = EndpointMode.BufferedInMemory;

        var sender = InvokeCreateSender(runtime);

        sender.ShouldBeOfType<BatchedSender>();
    }

    [Fact]
    public void describe_properties_returns_base_properties()
    {
        var properties = _endpoint.DescribeProperties();
        properties.ShouldNotBeNull();
    }

    [Fact]
    public void supports_all_modes()
    {
        InvokeSupportsMode(EndpointMode.Inline).ShouldBeTrue();
        InvokeSupportsMode(EndpointMode.BufferedInMemory).ShouldBeTrue();
        InvokeSupportsMode(EndpointMode.Durable).ShouldBeTrue();
    }
}
