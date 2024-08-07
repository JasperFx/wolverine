#pragma warning disable IDE0058
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace Wolverine.MessagePack.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _publishingHost;
    private IHost _receivingHost;

    [Fact]
    public async Task end_to_end_message_send_using_messagepack()
    {
        // Arrange
        var messageNameContent = "Message test";

        // Act
        var actual = await _publishingHost
            .TrackActivity()
            .AlsoTrack(_receivingHost)
            .SendMessageAndWaitAsync(new MessagePackMessage { Name = messageNameContent });

        // Assert
        actual.Received.SingleMessage<MessagePackMessage>()
            .ShouldNotBeNull()
            .ShouldBeOfType<MessagePackMessage>()
            .Name.ShouldBe(messageNameContent);
    }

    [Fact]
    public async Task end_to_end_record_message_send_using_messagepack()
    {
        // Arrange
        var messageNameContent = "Record message test";

        // Act
        var actual = await _publishingHost
            .TrackActivity()
            .AlsoTrack(_receivingHost)
            .SendMessageAndWaitAsync(new MessagePackRecordMessage(messageNameContent));

        // Assert
        actual.Received.SingleMessage<MessagePackRecordMessage>()
            .ShouldNotBeNull()
            .ShouldBeOfType<MessagePackRecordMessage>()
            .Name.ShouldBe(messageNameContent);
    }

    [Fact]
    public async Task end_to_end_keyless_message_send_using_messagepack()
    {
        // Arrange
        var messageNameContent = "Keyless message test";

        // Act
        var actual = await _publishingHost
            .TrackActivity()
            .AlsoTrack(_receivingHost)
            .SendMessageAndWaitAsync(new MessagePackKeylessMessage { Name = messageNameContent });

        // Assert
        actual.Received.SingleMessage<MessagePackKeylessMessage>()
            .ShouldNotBeNull()
            .ShouldBeOfType<MessagePackKeylessMessage>()
            .Name.ShouldBe(messageNameContent);
    }

    #region Test setup

    public async Task InitializeAsync()
    {
        var receivingTcpPort = PortFinder.GetAvailablePort();

        _publishingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMessagePackSerialization();
            opts.PublishMessage<MessagePackMessage>().ToPort(receivingTcpPort);
            opts.PublishMessage<MessagePackRecordMessage>().ToPort(receivingTcpPort);
            opts.PublishMessage<MessagePackKeylessMessage>().ToPort(receivingTcpPort);
        }).StartAsync();

        _receivingHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseMessagePackSerialization();
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
