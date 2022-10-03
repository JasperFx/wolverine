using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class response_queue_mechanics : IAsyncLifetime
{
    private IHost _host;
    private string theExpectedResponseQueueName;
    private RabbitMqEndpoint? theEndpoint;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MyApp";
                opts.UseRabbitMq();
            }).StartAsync();

        var options = _host.Services.GetRequiredService<WolverineOptions>();
        theExpectedResponseQueueName = $"myapp_response_{options.Advanced.UniqueNodeId}";
        
        theEndpoint = _host.GetRuntime().Endpoints.EndpointByName(RabbitMqTransport.ResponseEndpointName)
            .ShouldBeOfType<RabbitMqEndpoint>();

    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public void should_be_the_reply_uri()
    {
        var transport = _host.Get<WolverineOptions>().RabbitMqTransport();
        
        transport.ReplyEndpoint()
            .ShouldBeSameAs(theEndpoint);
    }

    [Fact]
    public void the_endpoint_exists()
    {
        theEndpoint.ShouldNotBeNull();
    }

    [Fact]
    public void the_endpoint_is_a_parallel_inline_listener()
    {
        theEndpoint.ListenerCount.ShouldBe(5);
        theEndpoint.Mode.ShouldBe(EndpointMode.Inline);
        theEndpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void should_be_marked_for_replies()
    {
        theEndpoint.IsUsedForReplies.ShouldBeTrue();
    }

    [Fact]
    public void should_be_marked_as_system_role()
    {
        theEndpoint.Role.ShouldBe(EndpointRole.System);
    }

    [Fact]
    public void is_using_the_expected_queue_name()
    {
        theEndpoint.QueueName.ShouldStartWith("wolverine.response");
    }

    [Fact]
    public void queue_should_be_non_durable()
    {
        var transport = _host.Get<WolverineOptions>().RabbitMqTransport();
        var queue = transport.Queues[theEndpoint.QueueName];
        
        queue.AutoDelete.ShouldBeTrue();
        queue.IsExclusive.ShouldBeFalse();
        queue.IsDurable.ShouldBeFalse();
            
            
    }
    
    
}