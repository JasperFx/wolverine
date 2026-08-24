using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

public class NatsListener : IListener, ISupportDeadLetterQueue, IReportConnectionState, ISupportLeaseRenewal
{
    private readonly NatsEndpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly CancellationTokenSource _cancellation;
    private readonly RetryBlock<NatsEnvelope> _complete;
    private readonly RetryBlock<NatsEnvelope> _defer;
    private readonly INatsSubscriber _subscriber;
    private readonly ISender _deadLetterSender;

    public IHandlerPipeline? Pipeline { get; private set; }

    // GH-3231: surface the NATS connection state (via the subscriber that owns the connection) so external monitors
    // can detect a listener whose connection has dropped while it still reports Accepting.
    public TransportConnectionState ConnectionState => _subscriber.ConnectionState;

    internal NatsListener(
        NatsEndpoint endpoint,
        INatsSubscriber subscriber,
        IWolverineRuntime runtime,
        IReceiver receiver,
        ILogger<NatsEndpoint> logger,
        ISender deadLetterSender,
        CancellationToken parentCancellation
    )
    {
        _endpoint = endpoint;
        _subscriber = subscriber;
        _runtime = runtime;
        _receiver = receiver;
        _logger = logger;
        _deadLetterSender = deadLetterSender;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
        Address = endpoint.Uri;

        _complete = new RetryBlock<NatsEnvelope>(
            async (envelope, _) =>
            {
                if (envelope.JetStreamMsg != null)
                {
                    await envelope.JetStreamMsg.AckAsync(
                        cancellationToken: _cancellation.Token
                    );
                }
            },
            logger,
            _cancellation.Token
        );

        _defer = new RetryBlock<NatsEnvelope>(
            async (envelope, _) =>
            {
                if (envelope.JetStreamMsg != null)
                {
                    // JetStream supports native NAK which will redeliver the message
                    await envelope.JetStreamMsg.NakAsync(
                        cancellationToken: _cancellation.Token
                    );
                }
                else
                {
                    // Core NATS doesn't have native requeue - republish the message to the subject
                    await _subscriber.RepublishAsync(envelope, _cancellation.Token);
                }
            },
            logger,
            _cancellation.Token
        );
    }

    public Uri Address { get; }

    public bool NativeDeadLetterQueueEnabled => _subscriber.SupportsNativeDeadLetterQueue;

    public async Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        if (envelope is not NatsEnvelope natsEnvelope || !NativeDeadLetterQueueEnabled ||
            natsEnvelope.JetStreamMsg == null)
        {
            return;
        }

        var metadata = natsEnvelope.JetStreamMsg.Metadata;
        if (metadata?.NumDelivered < (ulong)_endpoint.EffectiveMaxDeliveryAttempts)
        {
            return;
        }

        var attempts = (int)(metadata?.NumDelivered ?? 1);

        // Retain the poison message by forwarding a copy to the dead-letter subject BEFORE terminating,
        // so a terminate failure can't lose it. Terminating without a configured dead-letter subject drops
        // the message, so warn loudly in that case.
        if (!string.IsNullOrEmpty(_endpoint.DeadLetterSubject))
        {
            envelope.Attempts = attempts;
            DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
            envelope.Headers["x-dlq-original-subject"] = _endpoint.Subject;

            await _deadLetterSender.SendAsync(envelope);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Message {MessageId} exceeded {Attempts} delivery attempts on subject {Subject} but no dead-letter subject is configured; it will be terminated and dropped. Use DeadLetterTo(...) / ConfigureDeadLetterQueue(...) to retain poison messages.",
                envelope.Id,
                attempts,
                _endpoint.Subject
            );
        }

        // Terminate delivery on the JetStream consumer with a reason so the server stops redelivering and
        // records why the message was dead-lettered.
        await natsEnvelope.JetStreamMsg.AckTerminateAsync(
            $"wolverine: exceeded {attempts} delivery attempts ({exception.GetType().Name})",
            cancellationToken: _cancellation.Token
        );

        _logger.LogError(
            exception,
            "Message {MessageId} terminated after {Attempts} delivery attempts. Subject: {Subject}, DeadLetter: {DeadLetter}",
            envelope.Id,
            attempts,
            _endpoint.Subject,
            _endpoint.DeadLetterSubject ?? "(none)"
        );
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is NatsEnvelope natsEnvelope)
        {
            await _complete.PostAsync(natsEnvelope);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is NatsEnvelope natsEnvelope)
        {
            await _defer.PostAsync(natsEnvelope);
        }
    }

    /// <summary>
    /// GH-4048/GH-4053. The JetStream consumer's <c>AckWait</c>: how long the server leaves one delivery
    /// unacknowledged before redelivering it. Every successful <c>AckProgress</c> re-arms it for exactly this
    /// long again, which is the contract <c>LeaseRenewalTracker</c> assumes.
    /// </summary>
    public TimeSpan LeaseDuration => _endpoint.EffectiveAckWait;

    public TimeSpan MaximumLeaseExtension => _endpoint.MaximumAckExtension;

    /// <summary>
    /// GH-4053. Re-arm <c>AckWait</c> on deliveries that are still sitting in a native-ack execution lane, by
    /// sending JetStream's <c>AckProgress</c> (<c>+WPI</c>) for each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every renewal is a DoubleAck.</b> A bare <c>AckProgressAsync</c> is fire-and-forget -- it publishes to
    /// the message's <c>$JS.ACK.*</c> reply subject and returns without waiting for the server -- so it can
    /// neither fail nor report which messages the server refused. That is not merely a weaker signal, it defeats
    /// BOTH of the tracker's loss detectors at once: <c>RenewLeasesAsync</c> would return an empty refusal list
    /// on every tick, and because it also never threw, <c>LeaseRenewalTracker</c> would stamp <c>LastRenewedAt</c>
    /// on every envelope every tick, so its inferred-loss detector ("no renewal has succeeded for a full lease
    /// duration") could never fire either. A NativeAck endpoint would then hold envelopes in lanes with no
    /// working lease at all -- exactly the silent duplicate generator GH-4048 exists to prevent. <c>DoubleAck</c>
    /// turns the publish into a request/reply the server answers, so a dead connection or a deleted
    /// stream/consumer surfaces as an exception per message.
    /// </para>
    /// <para>
    /// The cost is one round trip per unsettled envelope per tick (tick = <c>AckWait</c>/2), and JetStream has no
    /// batch ack API to amortize it. That is affordable precisely because this mode bounds the unacked window with
    /// <c>MaxAckPending</c> (see <see cref="NatsEndpoint.EffectiveMaxAckPending" />): the renewal fan-out is
    /// twice the lane count, not the pull batch size. Renewals are issued concurrently so one slow reply cannot
    /// push a tick past its interval.
    /// </para>
    /// <para>
    /// <b>What this cannot detect:</b> JetStream has no negative reply for "you are too late". If <c>AckWait</c>
    /// has already expired and the message was redelivered, the server still accepts and answers the
    /// <c>AckProgress</c> for the stale delivery attempt. That case is covered by
    /// <see cref="MaximumLeaseExtension" /> and by the tracker's inferred-loss detector rather than here, which
    /// is why the ceiling matters on this transport.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<Envelope>> RenewLeasesAsync(IReadOnlyList<Envelope> envelopes,
        CancellationToken token)
    {
        // Only JetStream deliveries have a clock. A core NATS envelope has no INatsJSMsg and nothing to renew --
        // NatsEndpoint refuses that combination at bootstrap, so this is belt and braces.
        var candidates = envelopes
            .OfType<NatsEnvelope>()
            .Where(x => x.JetStreamMsg != null)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        if (_subscriber.ConnectionState == TransportConnectionState.Disconnected)
        {
            // Transient by contract, and cheaper to say up front than to fan out N doomed requests and wait for
            // each to time out. The tracker retries on the next tick and infers loss if this persists past a
            // full AckWait.
            throw new InvalidOperationException(
                $"The NATS connection for {Address} is disconnected, so no JetStream lease could be renewed");
        }

        // Bound a tick: NATS's own request timeout defaults to 5s, which can exceed the tick interval on a short
        // AckWait. A quarter of the lease leaves the tracker room to run its inferred-loss pass in the same tick.
        var budget = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, LeaseDuration.Ticks / 4));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(budget);

        var results = await Task.WhenAll(candidates.Select(e => renewOneAsync(e, timeout.Token)));

        var failed = new List<Envelope>();
        for (var i = 0; i < candidates.Length; i++)
        {
            if (!results[i])
            {
                failed.Add(candidates[i]);
            }
        }

        if (failed.Count == candidates.Length)
        {
            // Every single renewal failing is the signature of a connection- or consumer-level fault, not of N
            // independently lost leases. Report it as transient: keeping the envelopes is the conservative
            // choice (a deleted consumer redelivers nothing, so dropping them would lose messages outright),
            // and the tracker's inferred-loss detector still marks them lost a full AckWait later if it
            // really is permanent.
            throw new InvalidOperationException(
                $"Could not renew the JetStream lease on any of {candidates.Length} in-flight envelopes at {Address}");
        }

        // A PARTIAL failure is per-message, so report it as a lost lease. Erring this way is deliberate: a
        // false "lost" costs one redelivery after AckWait (the envelope is dropped from its lane unsettled,
        // never lost), while a false "renewed" is a handler running twice.
        return failed;
    }

    /// <summary>
    /// One <c>AckProgress</c>, returning false when the server would not answer it. Failures are logged at Debug
    /// and never rethrown from here -- <see cref="RenewLeasesAsync"/> owns the transient-vs-lost decision, and it
    /// needs to see the whole tick's outcome to make it.
    /// </summary>
    private async Task<bool> renewOneAsync(NatsEnvelope envelope, CancellationToken token)
    {
        try
        {
            await envelope.JetStreamMsg!.AckProgressAsync(new AckOpts { DoubleAck = true }, token);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e,
                "JetStream would not extend the AckWait on envelope {EnvelopeId} at {Uri}; no longer keeping it alive",
                envelope.Id, Address);
            return false;
        }
    }

    public async Task StartAsync()
    {
        await _subscriber.StartAsync(this, _receiver, _cancellation.Token);
    }

    public async ValueTask StopAsync()
    {
        // Stop new deliveries FIRST -- disposing the subscriber ends the consume loop and flushes the
        // durable batching window into the inbox -- while this listener's cancellation token is still
        // live, so the acks for everything just persisted still go out. Cancelling first made the
        // _complete RetryBlock silently drop every ack issued during the drain, and each dropped ack
        // became a JetStream redelivery (and a duplicate inbox insert) AckWait later, counting against
        // the consumer's MaxAckPending the whole time -- a back-pressure pause starved the restarted
        // listener of deliveries for the full AckWait. See GH-4026.
        await _subscriber.DisposeAsync();
        await _cancellation.CancelAsync();
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cancellation.IsCancellationRequested)
        {
            // Same ordering as StopAsync: no new deliveries, drain, then cancel
            await _subscriber.DisposeAsync();
            await _cancellation.CancelAsync();
        }

        _complete.Dispose();
        _defer.Dispose();
        _cancellation.Dispose();
    }

    internal static NatsListener Create(
        NatsEndpoint endpoint,
        NatsConnection connection,
        INatsJSContext? jetStreamContext,
        IWolverineRuntime runtime,
        IReceiver receiver,
        ILogger<NatsEndpoint> logger,
        ISender? deadLetterSender,
        CancellationToken cancellation,
        bool useJetStream,
        string? subscriptionPattern = null,
        ITenantSubjectMapper? tenantMapper = null
    )
    {
        INatsSubscriber subscriber;
        if (useJetStream)
        {
            var jsMapper = new JetStreamEnvelopeMapper(endpoint, tenantMapper);
            if (endpoint.MessageType != null)
            {
                jsMapper.ReceivesMessage(endpoint.MessageType);
            }
            subscriber = new JetStreamSubscriber(endpoint, connection, jetStreamContext!, logger, jsMapper, subscriptionPattern);
        }
        else
        {
            var mapper = new NatsEnvelopeMapper(endpoint, tenantMapper);
            if (endpoint.MessageType != null)
            {
                mapper.ReceivesMessage(endpoint.MessageType);
            }
            subscriber = new CoreNatsSubscriber(endpoint, connection, logger, mapper, subscriptionPattern);
        }

        return new NatsListener(
            endpoint,
            subscriber,
            runtime,
            receiver,
            logger,
            deadLetterSender ?? new NullSender(),
            cancellation
        );
    }
}
