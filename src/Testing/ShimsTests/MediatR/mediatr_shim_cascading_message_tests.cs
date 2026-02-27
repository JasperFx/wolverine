using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using Alba;

namespace Wolverine.Shims.Tests.MediatR;

public class mediatr_shim_cascading_message_tests
{
    private IHostBuilder _builder;
    public mediatr_shim_cascading_message_tests()
    {
        _builder = Host.CreateDefaultBuilder();
        _builder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(mediatr_shim_cascading_message_tests).Assembly);
            opts.UseMediatRHandlers();
        });
    }

    [Fact]
    public async Task mediatr_handler_can_return_cascading_message()
    {
        await using var host = await AlbaHost.For(_builder);

        // Reset static state
        CascadingMessageHandler.ReceivedData = null;

        // Invoke the message and wait for cascading messages to be processed
        await host.InvokeAsync(new CascadingMessage("cascade-test"));

        // Verify the cascading message was handled
        CascadingMessageHandler.ReceivedData.ShouldBe("cascade-test");
    }
}
