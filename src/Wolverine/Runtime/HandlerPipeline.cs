using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.Serialization.Encryption;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class HandlerPipeline : IHandlerPipeline
{
    private readonly CancellationToken _cancellation;
    private readonly ObjectPool<MessageContext> _contextPool;

    private readonly LightweightCache<Type, IExecutor> _executors;
    private readonly HandlerGraph _graph;

    private readonly WolverineRuntime _runtime;
    private readonly Endpoint _endpoint = null!;

    internal HandlerPipeline(WolverineRuntime runtime, IExecutorFactory executorFactory)
    {
        _graph = runtime.Handlers;
        _runtime = runtime;
        ExecutorFactory = executorFactory;
        _contextPool = runtime.ExecutionPool;
        _cancellation = runtime.Cancellation;

        Logger = runtime.MessageTracking;

        _executors = new LightweightCache<Type, IExecutor>(executorFactory.BuildFor);
    }
    
    internal HandlerPipeline(WolverineRuntime runtime, IExecutorFactory executorFactory, Endpoint endpoint)
    {
        _graph = runtime.Handlers;
        _runtime = runtime;
        _endpoint = endpoint;
        ExecutorFactory = executorFactory;
        _contextPool = runtime.ExecutionPool;
        _cancellation = runtime.Cancellation;

        Logger = runtime.MessageTracking;

        _executors = new LightweightCache<Type, IExecutor>(type => executorFactory.BuildFor(type, endpoint));
    }

    internal IExecutorFactory ExecutorFactory { get; }

    public IMessageTracker Logger { get; }
    public bool TelemetryEnabled { get; set; } = true;

    public Task InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        using var activity = TelemetryEnabled ? WolverineTracing.StartExecuting(envelope) : null;

        return InvokeAsync(envelope, channel, activity);
    }

    public async Task InvokeAsync(Envelope envelope, IChannelCallback channel, Activity? activity)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var context = _contextPool.Get();
            context.ReadEnvelope(envelope, channel);

            try
            {
                var continuation = await executeAsync(context, envelope, activity).ConfigureAwait(false);
                await continuation.ExecuteAsync(context, _runtime, DateTimeOffset.UtcNow, activity).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // It's shutting down, get out of here
            }
            catch (Exception e)
            {
                await channel.CompleteAsync(envelope).ConfigureAwait(false);

                // Gotta get the message out of here because it's something that
                // could never be handled
                Logger.LogException(e, envelope.Id);

                activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            }
            finally
            {
                _contextPool.Return(context);
            }
        }
        finally
        {
            activity?.Stop();
        }
    }

    public async ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
    {
        if (envelope.Message != null) return NullContinuation.Instance;

        // Try to deserialize
        try
        {
            if (RequiresEncryption(envelope)
                && envelope.ContentType != EncryptionHeaders.EncryptedContentType)
            {
                return new MoveToErrorQueue(new EncryptionPolicyViolationException(envelope));
            }

            var serializer = envelope.Serializer ?? _runtime.Options.DetermineSerializer(envelope);
            serializer.UnwrapEnvelopeIfNecessary(envelope);

            if (envelope.Data == null)
            {
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "Envelope does not have a message or deserialized message data");
            }

            if (envelope.Message != null)
            {
                return NullContinuation.Instance;
            }

            if (string.IsNullOrEmpty(envelope.MessageType))
            {
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "The envelope has no Message or MessageType name");
            }

            if (_graph.TryFindMessageType(envelope.MessageType, out var messageType))
            {
                if (serializer is IAsyncMessageSerializer asyncMessageSerializer)
                {
                    envelope.Message = await asyncMessageSerializer.ReadFromDataAsync(messageType, envelope).ConfigureAwait(false);
                }
                else
                {
                    envelope.Message = serializer.ReadFromData(messageType, envelope);
                }
            }
            else
            {
                return new NoHandlerContinuation(_runtime.MissingHandlers(), _runtime);
            }

            if (envelope.Message == null)
            {
                return new MoveToErrorQueue(new InvalidOperationException(
                    "No message body could be de-serialized from the raw data in this envelope"));
            }

            return NullContinuation.Instance;
        }
        catch (Exception? e)
        {
            return new MoveToErrorQueue(e);
        }
        finally
        {
            Logger.Received(envelope);
        }
    }

    private bool RequiresEncryption(Envelope envelope)
    {
        var options = _runtime.Options;

        return (envelope.Destination is not null
                    && options.RequiredEncryptedListenerUris.Contains(envelope.Destination))
               || (!string.IsNullOrEmpty(envelope.MessageType)
                    && _graph.TryFindMessageType(envelope.MessageType, out var type)
                    && options.RequiredEncryptedTypes.Contains(type));
    }

    private async Task<IContinuation> executeAsync(MessageContext context, Envelope envelope, Activity? activity)
    {
        if (envelope.IsExpired())
        {
            return DiscardEnvelope.Instance;
        }

        if (envelope.Message == null)
        {
            var deserializationResult = await TryDeserializeEnvelope(envelope).ConfigureAwait(false);
            if(deserializationResult is not NullContinuation)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Serialization Failure");
                return deserializationResult;
            }
        }
        else
        {
            Logger.Received(envelope);
        }

        if (envelope.IsResponse)
        {
            // If a reply listener is registered (from InvokeAsync), complete it directly.
            // If not (from PublishAsync + RequireResponse), fall through to normal handler execution
            // so the response can be handled by a registered message handler.
            if (_runtime.Replies.Complete(envelope))
            {
                return MessageSucceededContinuation.Instance;
            }
        }

        var executor = _executors[envelope.Message!.GetType()];

        return await executor.ExecuteAsync(context, _cancellation).ConfigureAwait(false);
    }
}
