using System;

namespace Wolverine.Runtime.RemoteInvocation;

internal interface IReplyListener : IDisposable
{
    Guid RequestId { get; }
    void Complete(Envelope envelope);
}