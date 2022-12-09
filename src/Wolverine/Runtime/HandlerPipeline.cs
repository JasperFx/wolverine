using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.ObjectPool;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class HandlerPipeline : IHandlerPipeline
{
    private readonly CancellationToken _cancellation;
    private readonly ObjectPool<MessageContext> _contextPool;
    private readonly HandlerGraph _graph;

    private readonly WolverineRuntime _runtime;

    private readonly LightweightCache<Type, IExecutor> _executors;

    internal HandlerPipeline(WolverineRuntime runtime, IExecutorFactory executorFactory)
    {
        _graph = runtime.Handlers;
        _runtime = runtime;
        ExecutorFactory = executorFactory;
        _contextPool = runtime.ExecutionPool;
        _cancellation = runtime.Cancellation;

        Logger = runtime.MessageLogger;

        _executors = new LightweightCache<Type, IExecutor>(executorFactory.BuildFor);
    }

    internal IExecutorFactory ExecutorFactory { get; }

    public IMessageLogger Logger { get; }

    public Task InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        using var activity = WolverineTracing.StartExecuting(envelope);

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
                // TODO -- pass the activity into IContinuation?
                var continuation = await executeAsync(context, envelope);
                await continuation.ExecuteAsync(context, _runtime, DateTimeOffset.Now);
            }
            catch (Exception e)
            {
                await channel.CompleteAsync(envelope);

                // Gotta get the message out of here because it's something that
                // could never be handled
                Logger.LogException(e, envelope.Id);
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

    [Obsolete]
    public async Task InvokeNowAsync(Envelope envelope, CancellationToken cancellation = default)
    {
        // The static one is the application's, so put that check on the outside
        if (_cancellation.IsCancellationRequested || cancellation.IsCancellationRequested)
        {
            return;
        }

        // Do this check on the outside of invoker as well
        if (envelope.Message == null)
        {
            throw new ArgumentNullException(nameof(envelope.Message));
        }

        var executor = _executors[envelope.Message.GetType()];
        await executor.InvokeInlineAsync(envelope, cancellation);
    }

    private bool tryDeserializeEnvelope(Envelope envelope, out IContinuation continuation)
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
                envelope.Message = serializer.ReadFromData(messageType, envelope);
            }
            else
            {
                continuation = new NoHandlerContinuation(_runtime.MissingHandlers(), _runtime);
                return false;
            }

            if (envelope.Message == null)
            {
                continuation = new MoveToErrorQueue(new InvalidOperationException(
                    "No message body could be de-serialized from the raw data in this envelope"));

                return false;
            }

            continuation = NullContinuation.Instance;
            return true;
        }
        catch (Exception? e)
        {
            continuation = new MoveToErrorQueue(e);
            return false;
        }
        finally
        {
            Logger.Received(envelope);
        }
    }

    private async Task<IContinuation> executeAsync(MessageContext context, Envelope envelope)
    {
        if (envelope.IsExpired())
        {
            return DiscardEnvelope.Instance;
        }

        if (envelope.Message == null)
        {
            if (!tryDeserializeEnvelope(envelope, out var serializationError))
            {
                return serializationError;
            }
        }

        Logger.ExecutionStarted(envelope);

        if (envelope.IsResponse)
        {
            _runtime.Replies.Complete(envelope);
            return MessageSucceededContinuation.Instance;
        }

        var executor =_executors[envelope.Message!.GetType()];

        var continuation = await executor.ExecuteAsync(context, _cancellation).ConfigureAwait(false);
        Logger.ExecutionFinished(envelope);

        return continuation;
    }


}