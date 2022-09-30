using System;

namespace Wolverine.Runtime.ResponseReply;

internal interface IReplyListener : IDisposable
{
    void Complete(Envelope envelope);
    Guid RequestId { get; }
}