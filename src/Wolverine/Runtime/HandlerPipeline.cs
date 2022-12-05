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


    private readonly AdvancedSettings _settings;

    private ImHashMap<Type, IExecutor> _executors =
        ImHashMap<Type, IExecutor>.Empty;


    internal HandlerPipeline(WolverineRuntime runtime, IExecutorFactory executorFactory)
    {
        _graph = runtime.Handlers;
        _runtime = runtime;
        ExecutorFactory = executorFactory;
        _contextPool = runtime.ExecutionPool;
        _cancellation = runtime.Cancellation;

        Logger = runtime.MessageLogger;

        _settings = runtime.Advanced;
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


    public async Task InvokeNowAsync(Envelope envelope, CancellationToken cancellation = default)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        if (envelope.Message == null)
        {
            throw new ArgumentNullException(nameof(envelope.Message));
        }

        var executor = ExecutorFor(envelope.Message.GetType());

        using var activity = WolverineTracing.StartExecuting(envelope);

        Logger.ExecutionStarted(envelope);

        var context = _contextPool.Get();
        context.ReadEnvelope(envelope, InvocationCallback.Instance);

        envelope.Attempts = 1;

        try
        {
            while (await executor.InvokeAsync(context, cancellation) == InvokeResult.TryAgain)
            {
                envelope.Attempts++;
            }

            // TODO -- Harden the inline sender. Feel good about buffered
            await context.FlushOutgoingMessagesAsync();
        }
        finally
        {
            Logger.ExecutionFinished(envelope);
            _contextPool.Return(context);
            activity?.Stop();
        }
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

        var executor = ExecutorFor(envelope.Message!.GetType());

        var continuation = await executor.ExecuteAsync(context, _cancellation).ConfigureAwait(false);
        Logger.ExecutionFinished(envelope);

        return continuation;
    }

    internal IExecutor ExecutorFor(Type messageType)
    {
        if (_executors.TryFind(messageType, out var executor))
        {
            return executor;
        }

        executor = ExecutorFactory.BuildFor(messageType);

        _executors = _executors.AddOrUpdate(messageType, executor);

        return executor;
    }
}