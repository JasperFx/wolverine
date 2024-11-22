namespace Wolverine.Transports.Sending;

public interface ISenderRequiresCallback : IDisposable
{
    void RegisterCallback(ISenderCallback senderCallback);
}

public interface ISender
{
    bool SupportsNativeScheduledSend { get; }
    Uri Destination { get; }
    Task<bool> PingAsync();
    ValueTask SendAsync(Envelope envelope);
}

