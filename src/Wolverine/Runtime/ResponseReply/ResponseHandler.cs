using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Baseline;

namespace Wolverine.Runtime.ResponseReply;

internal class ResponseHandler : IDisposable
{
    // TODO -- occasionally sweep this for orphans?
    private readonly ConcurrentDictionary<Guid, IReplyListener> _listeners = new();

#pragma warning disable VSTHRD200
    public Task<T> RegisterCallback<T>(Envelope envelope, TimeSpan timeout) where T : class
#pragma warning restore VSTHRD200
    {
        envelope.DeliverWithin = timeout; // Make the message expire so it doesn't cruft up the receivers
        var listener = new ReplyListener<T>(envelope.Id, this, timeout);
        _listeners.AddOrUpdate(envelope.Id, listener, (id, l) => listener);

        return listener.Task;
    }

    public bool HasListener(Guid messageId)
    {
        return _listeners.ContainsKey(messageId);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Value.SafeDispose();
        }
        
        _listeners.Clear();
    }

    internal void Unregister(IReplyListener replyListener)
    {
        _listeners.TryRemove(replyListener.RequestId, out var listener);
        replyListener.SafeDispose();
    }

    public void Complete(Envelope response)
    {
        if (_listeners.TryGetValue(response.ConversationId, out var listener))
        {
            listener.Complete(response);
        }
    }
}