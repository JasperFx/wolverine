using System;
using System.Threading.Tasks;

namespace Wolverine.Transports;

public interface IListener : IChannelCallback, IAsyncDisposable
{
    Uri Address { get; }

    /// <summary>
    /// Stop the receiving of any new messages, but leave any connection
    /// open for possible calls to Defer/Complete
    /// </summary>
    /// <returns></returns>
    ValueTask StopAsync();
}
