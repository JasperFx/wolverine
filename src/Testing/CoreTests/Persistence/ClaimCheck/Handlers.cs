using System.Collections.Concurrent;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// Static sink so receiver-side tests can assert on the message instance
/// the handler actually saw (after claim-check rehydration).
/// </summary>
public static class CapturedMessages
{
    public static readonly ConcurrentBag<object> Received = new();

    public static T LastOf<T>() where T : class
    {
        return Received.OfType<T>().Last();
    }

    public static void Reset() => Received.Clear();
}

public class ClaimCheckMessageHandler
{
    public void Handle(BlobByteArrayMessage message) => CapturedMessages.Received.Add(message);
    public void Handle(BlobStringMessage message) => CapturedMessages.Received.Add(message);
    public void Handle(MultiBlobMessage message) => CapturedMessages.Received.Add(message);
    public void Handle(PlainMessage message) => CapturedMessages.Received.Add(message);
}
