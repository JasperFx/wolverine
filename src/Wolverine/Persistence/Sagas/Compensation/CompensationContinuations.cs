using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5556. A saga's own handler failed after its retries (or with a terminal failure): compensate it, as its own
/// handler chain with its own transaction, then dead letter the failed message, tagged with how the compensation
/// went. Never compensates a compensation
/// </summary>
internal sealed class CompensateSaga : IContinuation
{
    private readonly MoveToErrorQueue _deadLetter;
    private readonly SagaChain _chain;
    private readonly CompensationTarget _target;

    public CompensateSaga(MoveToErrorQueue deadLetter, SagaChain chain, CompensationTarget target)
    {
        _deadLetter = deadLetter;
        _chain = chain;
        _target = target;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        var envelope = lifecycle.Envelope;
        var sagaId = envelope?.Message == null ? null : sagaIdOf(lifecycle, envelope);

        // A failure no Compensate method matches is dead lettered as it always was, and so is a message whose saga
        // was never there to begin with
        var compensates = envelope?.Message != null &&
                          _deadLetter.Exception is not (UnknownSagaException or IndeterminateSagaStateIdException) &&
                          SagaCompensationMethods.For(_chain.SagaType)
            .Select(envelope.Message.GetType(), ExceptionInfo.From(_deadLetter.Exception), _deadLetter.Exception) != null;

        if (envelope == null || sagaId == null || !compensates)
        {
            // Nothing to compensate on: dead letter it, as without compensation
            await _deadLetter.ExecuteAsync(lifecycle, runtime, now, activity).ConfigureAwait(false);
            return;
        }

        var tag = await compensateAsync(runtime, envelope, sagaId).ConfigureAwait(false);

        envelope.Headers[DeadLetterQueueConstants.DeadLetterTagHeader] = tag;
        await _deadLetter.ExecuteAsync(lifecycle, runtime, now, activity).ConfigureAwait(false);
    }

    private async Task<string> compensateAsync(IWolverineRuntime runtime, Envelope envelope, string sagaId)
    {
        var faultEnvelope = _target.BuildFaultEnvelope(envelope, _deadLetter.Exception, sagaId);
        var cancellation = runtime.Cancellation;

        try
        {
            // Test seam for "another message completes the saga before it is compensated"
            if ((runtime as WolverineRuntime)?.CompensationHooks.BeforeCompensating is { } beforeCompensating)
            {
                await beforeCompensating(_chain.SagaType, sagaId).ConfigureAwait(false);
            }

            await _target.Compensation.InvokeInlineAsync(faultEnvelope, cancellation).ConfigureAwait(false);
            return DeadLetterQueueConstants.CompensatedTag;
        }
        catch (UnknownSagaException)
        {
            runtime.Logger.LogWarning(
                "Saga {SagaType} {SagaId} was gone when {MessageType} {EnvelopeId} was to be compensated; it is dead lettered tagged {Tag}",
                _chain.SagaType.FullNameInCode(), sagaId, envelope.MessageType, envelope.Id,
                DeadLetterQueueConstants.CompensationSagaMissingTag);
            return DeadLetterQueueConstants.CompensationSagaMissingTag;
        }
        catch (Exception e)
        {
            runtime.Logger.LogError(e,
                "Compensating {MessageType} {EnvelopeId} on saga {SagaType} {SagaId} failed; it is dead lettered tagged {Tag}",
                envelope.MessageType, envelope.Id, _chain.SagaType.FullNameInCode(), sagaId,
                DeadLetterQueueConstants.CompensationFailedTag);

            await _target.TryMarkFailedAsync(faultEnvelope, cancellation, e).ConfigureAwait(false);
            return DeadLetterQueueConstants.CompensationFailedTag;
        }
    }

    // The id the saga handler resolved, else the message's saga id member, else the envelope's saga id header
    private string? sagaIdOf(IEnvelopeLifecycle lifecycle, Envelope envelope)
    {
        if (lifecycle is MessageContext { ResolvedSagaId: { } resolved }) return resolved.ToString();

        if (_chain.SagaIdMember != null)
        {
            var value = _chain.SagaIdMember switch
            {
                System.Reflection.PropertyInfo property => property.GetValue(envelope.Message),
                System.Reflection.FieldInfo field => field.GetValue(envelope.Message),
                _ => null
            };
            if (value != null && value.ToString().IsNotEmpty()) return value.ToString();
        }

        return envelope.SagaId.IsNotEmpty() ? envelope.SagaId : null;
    }

    public override string ToString() => $"Compensate saga {_chain.SagaType.Name}, then {_deadLetter}";
}

/// <summary>
/// GH-5556. A saga's compensation chain (its <c>Fault&lt;T&gt;</c> chain) gave up after its retries: mark the saga
/// CompensationFailed, once, and dead letter the fault tagged <c>compensation-failed</c>. A saga that was gone is
/// tagged <c>compensation-saga-missing</c> instead
/// </summary>
internal sealed class CompensationGaveUp : IContinuation
{
    private readonly MoveToErrorQueue _deadLetter;
    private readonly CompensationTarget _target;

    public CompensationGaveUp(MoveToErrorQueue deadLetter, CompensationTarget target)
    {
        _deadLetter = deadLetter;
        _target = target;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        var envelope = lifecycle.Envelope;
        if (envelope?.Message != null)
        {
            if (_deadLetter.Exception is UnknownSagaException)
            {
                envelope.Headers[DeadLetterQueueConstants.DeadLetterTagHeader] =
                    DeadLetterQueueConstants.CompensationSagaMissingTag;
            }
            else
            {
                envelope.Headers[DeadLetterQueueConstants.DeadLetterTagHeader] =
                    DeadLetterQueueConstants.CompensationFailedTag;

                var sagaId = lifecycle is MessageContext { ResolvedSagaId: { } resolved }
                    ? resolved.ToString()
                    : envelope.SagaId;
                var marker = new Envelope(envelope.Message)
                {
                    SagaId = sagaId,
                    TenantId = envelope.TenantId,
                    CorrelationId = envelope.CorrelationId,
                    ConversationId = envelope.Id,
                    Source = envelope.Source
                };
                await _target.TryMarkFailedAsync(marker, runtime.Cancellation, _deadLetter.Exception).ConfigureAwait(false);
            }
        }

        await _deadLetter.ExecuteAsync(lifecycle, runtime, now, activity).ConfigureAwait(false);
    }

    public override string ToString() => $"Mark the saga {_target.SagaType.Name} CompensationFailed, then {_deadLetter}";
}
