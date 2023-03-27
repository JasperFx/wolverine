using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace Wolverine.MemoryPack.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _publishingHost;
    private IHost _receivingHost;

    [Fact]
    public async Task end_to_end_message_send_using_memorypack()
    {
        // Arrange
        var messageNameContent = "Message test";

        // Act
        var actual = await _publishingHost
            .TrackActivity()
            .AlsoTrack(_receivingHost)
            .SendMessageAndWaitAsync(new MemoryPackMessage { Name = messageNameContent });

        // Assert
        actual.Received.SingleMessage<MemoryPackMessage>()
            .ShouldNotBeNull()
            .ShouldBeOfType<MemoryPackMessage>()
            .Name.ShouldBe(messageNameContent);
    }

    #region Test setup

    public async Task InitializeAsync()
    {
        var receivingTcpPort = PortFinder.GetAvailablePort();

        _publishingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMemoryPackSerialization();
            opts.PublishMessage<MemoryPackMessage>().ToPort(receivingTcpPort);
        }).StartAsync();

        _receivingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMemoryPackSerialization();
            opts.ListenAtPort(receivingTcpPort);
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receivingHost.StopAsync();
        await _publishingHost.StopAsync();
    }

    #endregion
}