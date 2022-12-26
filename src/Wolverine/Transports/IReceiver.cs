﻿namespace Wolverine.Transports;

public interface IReceiver : IDisposable
{
    ValueTask ReceivedAsync(IListener listener, Envelope[] messages);
    ValueTask ReceivedAsync(IListener listener, Envelope envelope);

    ValueTask DrainAsync();
}