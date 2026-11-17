using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Logging;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports.Sending;

public class InlineSendingAgentTests
{
    private static InlineSendingAgent buildAgent(ISender sender)
    {
        return new InlineSendingAgent(NullLogger.Instance, sender, new TcpEndpoint(2270),
            Substitute.For<IMessageTracker>(), new DurabilitySettings());
    }

    [Fact]
    public async Task dispose_async_prefers_async_disposal_of_the_sender()
    {
        var sender = Substitute.For<ISender, IAsyncDisposable>();
        var agent = buildAgent(sender);

        await agent.DisposeAsync();

        await ((IAsyncDisposable)sender).Received().DisposeAsync();
    }

    [Fact]
    public async Task dispose_async_falls_back_to_sync_disposal_of_the_sender()
    {
        var sender = Substitute.For<ISender, IDisposable>();
        var agent = buildAgent(sender);

        await agent.DisposeAsync();

        ((IDisposable)sender).Received().Dispose();
    }
}
