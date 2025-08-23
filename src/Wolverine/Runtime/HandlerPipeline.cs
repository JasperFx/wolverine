using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class HandlerPipeline : IHandlerPipeline
{
    private readonly CancellationToken _cancellation;
    private readonly ObjectPool<MessageContext> _contextPool;

    private readonly LightweightCache<Type, IExecutor> _executors;
    private readonly HandlerGraph _graph;

    private readonly WolverineRuntime _runtime;
    private readonly Endpoint _endpoint;

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
                var continuation = await executeAsync(context, envelope, activity);
                await continuation.ExecuteAsync(context, _runtime, DateTimeOffset.UtcNow, activity);
            }
            catch (ObjectDisposedException)
            {
                // It's shutting down, get out of here
            }
            catch (Exception e)
            {
                await channel.CompleteAsync(envelope);

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
        // Try to deserialize
        try
        {
            var serializer = envelope.Serializer ?? _runtime.Options.DetermineSerializer(envelope);

            if (envelope.Data == null)
            {
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "Envelope does not have a message or deserialized message data");
            }

            if (envelope.MessageType == null)
            {
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "The envelope has no Message or MessageType name");
            }

            if (_graph.TryFindMessageType(envelope.MessageType, out var messageType))
            {
                if (serializer is IAsyncMessageSerializer asyncMessageSerializer)
                {
                    envelope.Message = await asyncMessageSerializer.ReadFromDataAsync(messageType, envelope);
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

    private async Task<IContinuation> executeAsync(MessageContext context, Envelope envelope, Activity? activity)
    {
        if (envelope.IsExpired())
        {
            return DiscardEnvelope.Instance;
        }

        if (envelope.Message == null)
        {
            var deserializationResult = await TryDeserializeEnvelope(envelope);
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
            _runtime.Replies.Complete(envelope);
            return MessageSucceededContinuation.Instance;
        }

        var executor = _executors[envelope.Message!.GetType()];

        return await executor.ExecuteAsync(context, _cancellation);
    }
}