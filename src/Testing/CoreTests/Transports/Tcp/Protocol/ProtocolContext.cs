using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Tcp.Protocol;

[Collection("protocol")]
public abstract class ProtocolContext : IDisposable
{
    protected static int NextPort = 6005;
    private readonly TestingListeningAgent _listener;
    public readonly Uri Destination;
    private readonly IPAddress theAddress = IPAddress.Loopback;
    private readonly OutgoingMessageBatch theMessageBatch;
    private readonly int thePort = ++NextPort;


    protected StubReceiverCallback theReceiver = new();
    protected StubSenderCallback theSender = new();

    public ProtocolContext()
    {
        Destination = $"tcp://localhost:{thePort}/incoming".ToUri();
        _listener = new TestingListeningAgent(theReceiver, theAddress, thePort, "durable", CancellationToken.None);


        var messages = new[]
        {
            outgoingMessage(),
            outgoingMessage(),
            outgoingMessage(),
            outgoingMessage(),
            outgoingMessage(),
            outgoingMessage()
        };

        theMessageBatch = new OutgoingMessageBatch(Destination, messages);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }


    private Envelope outgoingMessage()
    {
        return new Envelope
        {
            Destination = Destination,
            Data = new byte[] { 1, 2, 3, 4, 5, 6, 7 }
        };
    }

    protected async Task afterSending()
    {
        _listener.Start();

        using var client = new TcpClient();
        if (Dns.GetHostName() == Destination.Host)
        {
            await client.ConnectAsync(IPAddress.Loopback, Destination.Port);
        }

        await client.ConnectAsync(Destination.Host, Destination.Port);

        await WireProtocol.SendAsync(client.GetStream(), theMessageBatch, null, theSender);
    }

    protected void allTheMessagesWereReceived()
    {
        theReceiver.MessagesReceived.Length.ShouldBe(theMessageBatch.Messages.Count);
        theReceiver.MessagesReceived.Select(x => x.Id)
            .ShouldHaveTheSameElementsAs(theMessageBatch.Messages.Select(x => x.Id));
    }
}

public class StubReceiverCallback : IReceiver
{
    public bool ThrowErrorOnReceived;

    public Envelope[] MessagesReceived { get; set; }

    public bool? WasAcknowledged { get; set; }

    public Exception FailureException { get; set; }

    public Uri Address { get; }
    public ListeningStatus Status { get; set; }

    public ValueTask DrainAsync()
    {
        return ValueTask.CompletedTask;
    }

    public int QueueCount => 0;


    public void Dispose()
    {
    }

    ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope[] messages)
    {
        if (ThrowErrorOnReceived)
        {
            throw new DivideByZeroException();
        }

        MessagesReceived = messages;

        return ValueTask.CompletedTask;
    }

    public ValueTask ReceivedAsync(IListener listener, Envelope envelope)
    {
        throw new NotImplementedException();
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
