using Microsoft.Extensions.Hosting;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class application_does_not_fail_without_rabbit_mq_transport_initialized
{
    [Fact]
    public async Task do_not_fail()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();
    }
}