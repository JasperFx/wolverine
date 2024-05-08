using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class response_queue_disabling : IAsyncLifetime
{
    private IHost _host;

    [Fact]
    public void reply_queue_should_not_be_declared()
    {
        var transport = _host.Get<WolverineOptions>().RabbitMqTransport();

        transport.ReplyEndpoint()
            .ShouldBeNull();
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MyApp";
                opts.UseRabbitMq()
                    .DisableSystemRequestReplyQueueDeclaration();
            }).StartAsync();
    }

    public Task DisposeAsync() => _host.StopAsync();
}
 