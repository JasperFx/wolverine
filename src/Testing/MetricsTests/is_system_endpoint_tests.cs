using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MetricsTests;

/// <summary>
/// GH-4665. "System endpoint" means Wolverine's own plumbing, and <c>EndpointRole.System</c> is what
/// records that. This used to test the <c>local</c> scheme instead, which swept in every user queue and
/// silently dropped their dead letters from the per-type and per-tenant metrics.
/// </summary>
public class is_system_endpoint_tests : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // An ordinary application queue. EndpointRole.Application, like every local queue
                // LocalTransport does not explicitly mark.
                opts.PublishMessage<SomeMetricsMessage>().ToLocalQueue("orders");
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData("rabbitmq://localhost/wolverine.response.abc123", true)]
    [InlineData("rabbitmq://localhost/wolverine.Response.ABC123", true)]
    [InlineData("redis://localhost/wolverine.response.node1", true)]
    // Azure Service Bus can prepend an application-owned prefix to its system queue names so that
    // several applications can share one namespace. The "wolverine.response" token survives that,
    // which is exactly why this check stays a Contains() rather than a StartsWith().
    [InlineData("asb://queue/my-project.wolverine.response.myapp.1", true)]
    // The local reply queue is the same plumbing as those wolverine.response queues.
    [InlineData("local://replies", true)]
    // LocalTransport marks these three EndpointRole.System.
    [InlineData("local://durable", true)]
    [InlineData("local://scheduled", true)]
    [InlineData("local://agents", true)]
    // GH-4665: a user's own local queue is NOT system traffic. This was true before the fix.
    [InlineData("local://orders", false)]
    [InlineData("local://default", false)]
    // An unregistered destination is not system traffic either -- counting a stray beats silently
    // dropping a user's dead letters.
    [InlineData("local://never-registered", false)]
    [InlineData("rabbitmq://localhost/my-queue", false)]
    [InlineData("tcp://localhost:5000", false)]
    public void should_identify_system_endpoints(string uriString, bool expected)
    {
        var uri = new Uri(uriString);

        // twice, so both the computing and the cached path are exercised
        _runtime.IsSystemEndpoint(uri).ShouldBe(expected);
        _runtime.IsSystemEndpoint(uri).ShouldBe(expected);
    }

    [Fact]
    public void null_uri_is_not_system_endpoint()
    {
        _runtime.IsSystemEndpoint(null).ShouldBeFalse();
    }
}

public record SomeMetricsMessage(string Name);

public static class SomeMetricsMessageHandler
{
    public static void Handle(SomeMetricsMessage message)
    {
    }
}
