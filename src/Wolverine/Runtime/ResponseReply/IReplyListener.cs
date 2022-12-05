using System;

namespace Wolverine.Runtime.ResponseReply;

internal interface IReplyListener : IDisposable
{
    Guid RequestId { get; }
    void Complete(Envelope envelope);
}