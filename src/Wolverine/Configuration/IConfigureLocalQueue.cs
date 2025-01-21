using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

#region sample_IConfigureLocalQueue

/// <summary>
/// Helps mark a handler to configure the local queue that its messages
/// would be routed to. It's probably only useful to use this with "sticky" handlers
/// that run on an isolated local queue
/// </summary>
public interface IConfigureLocalQueue
{
    static abstract void Configure(LocalQueueConfiguration configuration);
}

#endregion