using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports.Tcp;

public class ping_handling
{
    [Fact]
    public async Task ping_happy_path_with_tcp()
    {
        using (var runtime = WolverineHost.For(opts => { opts.ListenAtPort(2222); }))
        {
            var sender = new BatchedSender(new TcpEndpoint(2222), new SocketSenderProtocol(),
                CancellationToken.None, NullLogger.Instance);

            sender.RegisterCallback(new StubSenderCallback());

            await sender.PingAsync();
        }
    }

    [Fact]
    public async Task ping_sad_path_with_tcp()
    {
        var sender = new BatchedSender(new TcpEndpoint(3322), new SocketSenderProtocol(),
            CancellationToken.None, NullLogger.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () => { await sender.PingAsync(); });
    }
}

public class StubSenderCallback : ISenderCallback
{
    public bool Succeeded { get; set; }

    public bool TimedOut { get; set; }

    public bool SerializationFailed { get; set; }

    public bool QueueDoesNotExist { get; set; }

    public bool ProcessingFailed { get; set; }

    public Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        Succeeded = true;
        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkTimedOutAsync(OutgoingMessageBatch outgoing)
    {
        TimedOut = true;
        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkSerializationFailureAsync(OutgoingMessageBatch outgoing)
    {
        SerializationFailed = true;
        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing)
    {
        QueueDoesNotExist = true;
        return Task.CompletedTask;
    }

    Task ISenderCallback.MarkProcessingFailureAsync(OutgoingMessageBatch outgoing)
    {
        ProcessingFailed = true;
        return Task.CompletedTask;
    }

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception)
    {
        throw new NotImplementedException();
    }

    public Task MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing)
    {
        throw new NotImplementedException();
    }

    public Task MarkSuccessfulAsync(Envelope outgoing)
    {
        Succeeded = true;
        return Task.CompletedTask;
    }

    public Task MarkProcessingFailureAsync(Envelope outgoing, Exception? exception)
    {
        ProcessingFailed = true;
        return Task.CompletedTask;
    }

    public Task StopSending()
    {
        throw new NotImplementedException();
    }
}