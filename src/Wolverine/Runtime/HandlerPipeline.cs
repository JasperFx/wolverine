using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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

        // No runtime check for HandlerExecutionDiagnosticsEnabled — diagnostic tag
        // stamping is baked into the generated handler chain via
        // ApplyExecutionDiagnosticTagsFrame when the flag is set at codegen time.
        // See GH-2694.
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
                // The pipeline's last line of defense. Containment lives in a guarded helper so a
                // failure in the recovery path itself (e.g. an OutOfMemoryException re-thrown by the
                // allocating CompleteAsync / logging calls) can never escape InvokeAsync, fault the
                // receiver loop, and silently stop the listener. See GH-3111.
                await RecoverFromFailedProcessingAsync(channel, envelope, e, Logger, _runtime.Logger, activity)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// The pipeline's final, allocation-safe recovery step. Normal failures are acked out of the way
    /// and logged exactly as before; the difference is that if that recovery work itself throws — which
    /// happens when the original failure is resource exhaustion, because <see cref="IChannelCallback.CompleteAsync"/>
    /// and structured logging both allocate at the memory ceiling — the failure is contained here instead of
    /// escaping <c>InvokeAsync</c>. An exception escaping the pipeline faults the receiver loop, stops the
    /// listener, and lets the host exit cleanly (exit code 0), which an orchestrator reads as success and
    /// restarts straight back into the same un-acked poison message → permanent crash loop. A single
    /// un-recoverable envelope must never take the whole host down. See GH-3111.
    /// </summary>
    internal static async ValueTask RecoverFromFailedProcessingAsync(
        IChannelCallback channel,
        Envelope envelope,
        Exception exception,
        IMessageTracker tracker,
        ILogger logger,
        Activity? activity)
    {
        try
        {
            // Gotta get the message out of here because it's something that
            // could never be handled
            await channel.CompleteAsync(envelope).ConfigureAwait(false);

            tracker.LogException(exception, envelope.Id);

            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }
        catch (Exception recoveryFailure)
        {
            // The recovery path itself failed — almost always because the original failure was an
            // OutOfMemoryException and CompleteAsync / structured logging also allocate. Swallow it so
            // it cannot escape InvokeAsync and stop the listener; the broker will redeliver the
            // still-un-acked envelope, but the host stays up and keeps processing other messages.
            TryLogContainedFailure(logger, envelope, exception, recoveryFailure, activity);
        }
    }

    private static void TryLogContainedFailure(ILogger logger, Envelope envelope, Exception original,
        Exception recoveryFailure, Activity? activity)
    {
        try
        {
            var fatal = original is OutOfMemoryException || recoveryFailure is OutOfMemoryException;

            logger.LogError(recoveryFailure,
                "Wolverine could not recover from a {Severity} failure while processing envelope {Id} " +
                "(original failure: {Original}). The message was not acknowledged or dead-lettered and may be " +
                "redelivered by the broker; the listener is being kept alive to avoid a poison-message crash loop. See GH-3111.",
                fatal ? "fatal resource-exhaustion" : "cascading", envelope.Id, original.GetType().Name);

            activity?.SetStatus(ActivityStatusCode.Error, original.GetType().Name);
        }
        catch
        {
            // Even minimal structured logging allocates and can itself fail at the memory ceiling.
            // There is nothing more we can safely do here; never let the containment path throw.
        }
    }

    public async ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
    {
        if (envelope.Message != null) return NullContinuation.Instance;

        // Opt-in via WolverineOptions.Tracking.DeserializationSpanEnabled.
        // The span only starts when the flag is on AND we have a current Activity
        // listener — ActivitySource.StartActivity returns null otherwise, which
        // keeps the no-op cost trivial.
        using var activity = _runtime.Options.Tracking.DeserializationSpanEnabled
            ? WolverineTracing.ActivitySource.StartActivity(WolverineTracing.Deserialize, ActivityKind.Internal)
            : null;

        // Try to deserialize
        try
        {
            if (RequiresEncryption(envelope)
                && envelope.ContentType != EncryptionHeaders.EncryptedContentType)
            {
                return new MoveToErrorQueue(new EncryptionPolicyViolationException(envelope));
            }

            var serializer = envelope.Serializer ?? serializerFor(envelope);
            serializer.UnwrapEnvelopeIfNecessary(envelope);

            if (envelope.Data == null)
            {
                throw new ArgumentOutOfRangeException(nameof(envelope),
                    "Envelope does not have a message or deserialized message data");
            }

            activity?.SetTag(WolverineTracing.PayloadSizeBytes, envelope.Data.Length);

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
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            return new MoveToErrorQueue(e);
        }
        finally
        {
            Logger.Received(envelope);
        }
    }

    // Resolve the serializer for an envelope whose runtime-only Serializer reference
    // is no longer set. This happens on replay paths (scheduled retry, durable
    // recovery) where the envelope is rehydrated from storage: ContentType and
    // Destination survive, but Serializer does not. Resolve from the originating
    // endpoint first so endpoint-scoped serializers are honored on replay exactly
    // as they are at first receipt. This matters most for the MassTransit and
    // NServiceBus interop serializers wired by UseMassTransitInterop() /
    // UseNServiceBusInterop(): they register only on the listener endpoint, never in
    // the global content-type registry. Without this, DetermineSerializer falls back
    // to the default JSON serializer, which deserializes the un-unwrapped interop
    // envelope root and yields an all-default message. Mirrors DeadLetterEnvelope.TryReadData.
    private IMessageSerializer serializerFor(Envelope envelope)
    {
        if (envelope.ContentType.IsNotEmpty())
        {
            // _endpoint is set on per-listener pipelines and is the most reliable
            // source; fall back to the persisted Destination for the global pipeline.
            var endpoint = _endpoint ?? endpointFor(envelope.Destination);
            var serializer = endpoint?.TryFindSerializer(envelope.ContentType);
            if (serializer != null)
            {
                return serializer;
            }
        }

        return _runtime.Options.DetermineSerializer(envelope);
    }

    private Endpoint? endpointFor(Uri? destination)
    {
        return destination == null ? null : _runtime.Endpoints.EndpointFor(destination);
    }

    private bool RequiresEncryption(Envelope envelope)
    {
        var options = _runtime.Options;

        // Use the listener's own URI, not envelope.Destination: the latter is sender-
        // controlled and not populated on broker transports (Rabbit/Kafka/SB).
        // For per-type enforcement, defer to IsEncryptionRequired so the check
        // mirrors the polymorphic send-side rule (CanBeCastTo<T>) and a plaintext
        // envelope for a concrete subtype of an interface/abstract marker is rejected.
        return (_endpoint?.Uri is not null
                    && options.RequiredEncryptedListenerUris.Contains(_endpoint.Uri))
               || (!string.IsNullOrEmpty(envelope.MessageType)
                    && _graph.TryFindMessageType(envelope.MessageType, out var type)
                    && options.IsEncryptionRequired(type));
    }

    private async Task<IContinuation> executeAsync(MessageContext context, Envelope envelope, Activity? activity)
    {
        if (envelope.IsExpired())
        {
            return new DiscardEnvelope(new EnvelopeExpiredException(envelope));
        }

        if (envelope.Message == null)
        {
            var deserializationResult = await TryDeserializeEnvelope(envelope).ConfigureAwait(false);
            if (deserializationResult is not NullContinuation)
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
