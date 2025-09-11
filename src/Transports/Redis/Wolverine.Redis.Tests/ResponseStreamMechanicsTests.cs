using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests;

public class ResponseStreamMechanics : IAsyncLifetime
{
    private IHost _host = null!;
    private RedisStreamEndpoint _endpoint = null!;
    private string _expectedStreamKey = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MyApp";
                opts.UseRedisTransport("localhost:6379").AutoProvision();
            }).StartAsync();

        var runtime = _host.GetRuntime();
        var options = runtime.Options;
        var transport = options.Transports.GetOrCreate<RedisTransport>();

        _expectedStreamKey = $"wolverine.response.{options.ServiceName}.{options.Durability.AssignedNodeNumber}".ToLowerInvariant();

        // Should be created by transport initialization as a system endpoint
        var reply = transport.ReplyEndpoint();
        reply.ShouldNotBeNull();

        _endpoint = reply.ShouldBeOfType<RedisStreamEndpoint>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public void the_endpoint_exists()
    {
        _endpoint.ShouldNotBeNull();
    }

    [Fact]
    public void should_be_marked_for_replies()
    {
        _endpoint.IsUsedForReplies.ShouldBeTrue();
    }

    [Fact]
    public void should_be_marked_as_system_role()
    {
        _endpoint.Role.ShouldBe(EndpointRole.System);
    }

    [Fact]
    public void is_using_the_expected_stream_key()
    {
        _endpoint.StreamKey.ShouldStartWith("wolverine.response.myapp");
        _endpoint.StreamKey.ShouldBe(_expectedStreamKey);
    }

    [Fact]
    public void should_have_expected_uri()
    {
        var expected = new Uri($"redis://stream/0/{_expectedStreamKey}?consumerGroup=wolverine-replies");
        _endpoint.Uri.ShouldBe(expected);
    }
}

public class ResponseStreamDisabling : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var t = opts.UseRedisTransport("localhost:6379").AutoProvision();
                t.SystemQueuesEnabled = false;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public void disable_system_streams()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<RedisTransport>();
        var systemEndpoints = transport.Endpoints().Where(x => x.Role == EndpointRole.System).ToArray();
        systemEndpoints.Any().ShouldBeFalse();
    }
}

