using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Util;

namespace Wolverine.Runtime.RemoteInvocation;

public interface IReplyTracker : IDisposable
{
#pragma warning disable VSTHRD200
    Task<T> RegisterListener<T>(Envelope envelope, CancellationToken cancellationToken, TimeSpan timeout)
#pragma warning restore VSTHRD200
        ;

    void Complete(Envelope response);
    int AssignedNodeNumber { get; set; }
}

internal class ReplyTracker : IReplyTracker
{
    public int AssignedNodeNumber { get; set; }
    private readonly ConcurrentDictionary<Guid, IReplyListener> _listeners = new();
    private readonly ILogger<ReplyTracker> _logger;

    public ReplyTracker(ILogger<ReplyTracker> logger, int assignedNodeNumber)
    {
        AssignedNodeNumber = assignedNodeNumber;
        _logger = logger;
    }

#pragma warning disable VSTHRD200
    public Task<T> RegisterListener<T>(Envelope envelope, CancellationToken cancellationToken, TimeSpan timeout)
#pragma warning restore VSTHRD200
    {
        envelope.DeliverWithin = timeout; // Make the message expire so it doesn't cruft up the receivers
        var listener = new ReplyListener<T>(envelope, this, timeout, cancellationToken);
        _listeners.AddOrUpdate(envelope.Id, listener, (_, _) => listener);
        
        _logger.LogDebug("Registering a reply listener for message type {MessageType} and conversation id {ConversationId} on Node {NodeNumber}", typeof(T).ToMessageTypeName(), envelope.ConversationId, AssignedNodeNumber);

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
                _logger.LogDebug("Successfully completed a reply listener for conversation id {ReplyId} with message type {MessageTypeName} on Node {NodeNumber}", response.ConversationId, response.MessageType, AssignedNodeNumber);
            }
            else
            {
                _logger.LogError("Unable to find a registered reply listener for conversation id {ReplyId} with message type {MessageType} on Node {NodeNumber}. The listener may have previously timed out", response.ConversationId, response.MessageType, AssignedNodeNumber);
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
        _listeners.TryRemove(replyListener.RequestId, out var _);
        replyListener.SafeDispose();
    }
}