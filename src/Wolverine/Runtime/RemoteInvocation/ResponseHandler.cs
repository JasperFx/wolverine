using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Runtime.RemoteInvocation;

public interface IReplyTracker : IDisposable
{
#pragma warning disable VSTHRD200
    Task<T> RegisterListener<T>(Envelope envelope, CancellationToken cancellationToken, TimeSpan timeout)
#pragma warning restore VSTHRD200
    ;

    void Complete(Envelope response);
}

internal class ReplyTracker : IReplyTracker
{
    // TODO -- occasionally sweep this for orphans?
    private readonly ConcurrentDictionary<Guid, IReplyListener> _listeners = new();
    private readonly ILogger<ReplyTracker> _logger;

    public ReplyTracker(ILogger<ReplyTracker> logger)
    {
        _logger = logger;
    }

#pragma warning disable VSTHRD200
    public Task<T> RegisterListener<T>(Envelope envelope, CancellationToken cancellationToken, TimeSpan timeout)
#pragma warning restore VSTHRD200
    {
        envelope.DeliverWithin = timeout; // Make the message expire so it doesn't cruft up the receivers
        var listener = new ReplyListener<T>(envelope.Id, this, timeout, cancellationToken);
        _listeners.AddOrUpdate(envelope.Id, listener, (id, l) => listener);

        return listener.Task;
    }

    public void Dispose()
    {
        foreach (var listener in _listeners) listener.Value.SafeDispose();

        _listeners.Clear();
    }

    public void Complete(Envelope response)
    {
        try
        {
            if (_listeners.TryGetValue(response.ConversationId, out var listener))
            {
                listener.Complete(response);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to complete a response for envelope conversation id {ReplyId}",
                response.ConversationId);
        }
    }

    public bool HasListener(Guid messageId)
    {
        return _listeners.ContainsKey(messageId);
    }

    internal void Unregister(IReplyListener replyListener)
    {
        _listeners.TryRemove(replyListener.RequestId, out var listener);
        replyListener.SafeDispose();
    }
}