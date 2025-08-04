#pragma warning disable IDE0058
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Protobuf.Tests.Messages;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Protobuf.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _publishingHost;
    private IHost _receivingHost;

    [Fact]
    public async Task end_to_end_message_send_using_protobuf()
    {
        // Arrange
        var messageNameContent = "Message test";

        // Act
        var actual = await _publishingHost
            .TrackActivity()
            .AlsoTrack(_receivingHost)
            .SendMessageAndWaitAsync(new TestProtobufMessage { Name = messageNameContent });

        // Assert
        actual.Received.SingleMessage<TestProtobufMessage>()
            .ShouldNotBeNull()
            .ShouldBeOfType<TestProtobufMessage>()
            .Name.ShouldBe(messageNameContent);
    }

    #region Test setup

    public async Task InitializeAsync()
    {
        var receivingTcpPort = PortFinder.GetAvailablePort();

        _publishingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseProtobufSerialization();
            opts.PublishMessage<TestProtobufMessage>().ToPort(receivingTcpPort);
            opts.ApplicationAssembly = typeof(TestProtobufMessage).Assembly;

        }).StartAsync();

        _receivingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseProtobufSerialization();
            opts.ListenAtPort(receivingTcpPort);
            opts.ApplicationAssembly = typeof(TestProtobufMessage).Assembly;
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receivingHost.StopAsync();
        await _publishingHost.StopAsync();
    }

    #endregion
}
