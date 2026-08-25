using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf.Collections;
using Grpc.Core;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Pubsub.Internal;

public abstract class PubsubListener : IListener, ISupportDeadLetterQueue, IReportConnectionState
{
    protected readonly CancellationTokenSource _cancellation = new();
    protected readonly RetryBlock<Envelope> _deadLetter;
    protected readonly PubsubEndpoint? _deadLetterTopic;
    protected readonly PubsubEndpoint _endpoint;
    protected readonly ILogger _logger;
    protected readonly IReceiver _receiver;
    protected readonly RetryBlock<Envelope> _requeue;
    protected readonly IWolverineRuntime _runtime;
    protected readonly PubsubTransport _transport;

    protected Task _task;

    // GH-3237: the Pub/Sub SDK hides the streaming-pull connection, so this state is derived only from the
    // retry loop below and may only ever degrade (Reconnecting/Disconnected). Never Connected — the resting
    // state for a healthy listener is Unknown; use LastQueueActivityAt/loop-health for liveness.
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Unknown;

    public TransportConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// The connection (default or per-tenant) this listener consumes and, for requeue/dead-letter, re-publishes over.
    /// </summary>
    protected readonly PubsubClientSet _clients;

    /// <summary>GH-4052. Only populated when the endpoint is in EndpointMode.NativeAck.</summary>
    private readonly PubsubHeldDeliveries _held = new();

    private bool holdsUntilSettled => _endpoint.Mode == EndpointMode.NativeAck;
    private readonly IPubsubEnvelopeMapper _mapper;

    /// <summary>
    /// GH-4066. Warns when a delivery is held longer than the ack extension budget, at which point Pub/Sub
    /// begins redelivering it into a second, concurrent execution.
    /// </summary>
    private readonly AckExtensionWatchdog _ackExtensionWatchdog;

    public PubsubListener(
        PubsubEndpoint endpoint,
        PubsubTransport transport,
        IReceiver receiver,
        IWolverineRuntime runtime,
        PubsubClientSet clients
    )
    {
        if (clients.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        _mapper = endpoint.BuildMapper(runtime);

        _endpoint = endpoint;
        _transport = transport;
        _receiver = receiver;
        _runtime = runtime;
        _clients = clients;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubListener>();
        _ackExtensionWatchdog = new AckExtensionWatchdog(_endpoint.Uri, _endpoint.Client.MaxTotalAckExtension, _logger);

        if (_endpoint.DeadLetterName.IsNotEmpty())
        {
            _deadLetterTopic = _transport.Topics[_endpoint.DeadLetterName];

            NativeDeadLetterQueueEnabled = true;
        }

        _deadLetter = new RetryBlock<Envelope>(async (e, _) =>
        {
            if (_deadLetterTopic is null)
            {
                return;
            }
            await _deadLetterTopic.SendMessageAsync(e, _logger, _clients);
        }, _logger, runtime.Cancellation);

        _requeue = new RetryBlock<Envelope>(async (e, _) =>
        {
            await _endpoint.SendMessageAsync(e, _logger, _clients);
        }, _logger, runtime.Cancellation);

        _task = StartAsync();
    }

    /// <summary>
    /// The subscription this listener pulls from, resolved for its connection's project (default or tenant).
    /// </summary>
    protected SubscriptionName ListeningSubscriptionName => _endpoint.SubscriptionNameFor(_clients.ProjectId);

    public Uri Address => _endpoint.Uri;

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        // GH-4052. Pub/Sub has no per-message settle API, so under NativeAck the "ack" is the subscriber
        // callback finally being allowed to return Ack. Outside NativeAck this stays the no-op it always
        // was: the callback already returned Ack the moment the envelope was handed to the receiver.
        if (holdsUntilSettled && _held.TryFind(envelope, out var delivery))
        {
            delivery.Succeeded();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        // GH-4052. Under NativeAck a Nack IS the requeue -- the spike measured redelivery in ~10ms -- and it
        // is the correct one: the delivery was never settled, so republishing a copy would put a SECOND
        // message on the subscription on top of the redelivery Pub/Sub is about to perform anyway.
        if (holdsUntilSettled && _held.TryFind(envelope, out var delivery))
        {
            delivery.Failed();
            return;
        }

        await _requeue.PostAsync(envelope);
    }

    public async Task<bool> TryRequeueAsync(Envelope envelope)
    {
        if (holdsUntilSettled && _held.TryFind(envelope, out var delivery))
        {
            delivery.Failed();
            return true;
        }

        await _requeue.PostAsync(envelope);

        return true;
    }

    public async ValueTask StopAsync()
    {
        // GH-4052 (spike section 4c). A held callback that never returns wedges SubscriberClient.StopAsync
        // forever. Everything still held is unsettled by definition, so Nack is both correct and the only
        // answer that lets shutdown finish -- Pub/Sub redelivers it to whoever is still running.
        _held.NackAll();

        await _cancellation.CancelAsync();
    }

    public async ValueTask DisposeAsync()
    {
         await _cancellation.CancelAsync();
        _cancellation.Dispose();
        _task.SafeDispose();
        _requeue.SafeDispose();
        _deadLetter.SafeDispose();
        await _ackExtensionWatchdog.DisposeAsync();
    }

    public bool NativeDeadLetterQueueEnabled { get; }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        DeadLetterQueueConstants.StampFailureMetadata(envelope, exception);
        return _deadLetter.PostAsync(envelope);
    }

    public abstract Task StartAsync();

    protected async Task listenForMessagesAsync(Func<Task> listenAsync)
    {
        var retryCount = 0;

        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                // Back to the resting state for each fresh attempt; only a real failure below may degrade it
                _connectionState = TransportConnectionState.Unknown;

                await listenAsync();
            }
            catch (TaskCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            // This is a know issue at the moment:
            // https://github.com/googleapis/google-cloud-java/issues/4220
            // https://stackoverflow.com/questions/60012138/google-cloud-function-pulling-from-pub-sub-subscription-throws-exception-deadl
            catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                _connectionState = TransportConnectionState.Reconnecting;

                _logger.LogError(ex,
                    "{Uri}: Google Cloud Platform Pub/Sub returned \"DEADLINE_EXCEEDED\", attempting to restart listener.",
                    _endpoint.Uri);

                _task.SafeDispose();
                _task = StartAsync();

                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
            {
                _logger.LogInformation("{Uri}: Listener canceled, shutting down listener...", _endpoint.Uri);

                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                if (retryCount > _endpoint.Client.RetryPolicy.MaxRetryCount)
                {
                    _connectionState = TransportConnectionState.Disconnected;

                    _logger.LogError(ex, "{Uri}: Max retry attempts reached, unable to restart listener.",
                        _endpoint.Uri);

                    throw;
                }

                _connectionState = TransportConnectionState.Reconnecting;

                _logger.LogError(
                    ex,
                    "{Uri}: Error while trying to retrieve messages from Google Cloud Platform Pub/Sub, attempting to restart listener ({RetryCount}/{MaxRetryCount})...",
                    _endpoint.Uri,
                    retryCount,
                    _endpoint.Client.RetryPolicy.MaxRetryCount
                );

                var retryDelay = (int)Math.Pow(2, retryCount) * _endpoint.Client.RetryPolicy.RetryDelay;

                await Task.Delay(retryDelay, _cancellation.Token);
            }
        }
    }

    /// <summary>
    /// The <see cref="SubscriberClient.Settings" /> every listener runs on. Both the flow control bound and the
    /// ack deadline extension budget are set explicitly here rather than left to the SDK defaults, so that
    /// <see cref="PubsubClientOptions" /> is honoured on every endpoint mode. See
    /// <see cref="PubsubClientOptions.MaxTotalAckExtension" /> for why that budget is a deliberate choice.
    /// </summary>
    protected SubscriberClient.Settings buildSubscriberSettings()
    {
        return BuildSubscriberSettings(_endpoint.Client);
    }

    internal static SubscriberClient.Settings BuildSubscriberSettings(PubsubClientOptions options)
    {
        return new SubscriberClient.Settings
        {
            // GH-4067. MaxOutstandingMessages is a bound on the WHOLE SubscriberClient, and ClientCount does
            // NOT multiply it.
            //
            // This previously carried the "Remarks" paragraph from Google's own docs, which says a single
            // SubscriberClient creates multiple SubscriberServiceApiClient instances "and each will observe the
            // flow control settings independently". Measured against the real client library (3.24.0), that is
            // not what happens. Observed peak concurrently in-flight vs. MaxOutstandingElementCount was exactly
            // 1 -> 1, 3 -> 3, 8 -> 8 and 1000 -> 1000; with ClientCount = 4 and a limit of 3 the peak was 3,
            // not 12. Structurally the SDK builds one Flow inside StartAsync() and passes it to every
            // SingleChannel, and SubscriberClientImpl holds no per-client Flow -- one Flow, shared.
            //
            // Sizing a listener off the old comment would over-provision by a factor of ClientCount, so treat
            // MaxOutstandingMessages as the total in-flight ceiling for the endpoint.
            FlowControlSettings =
                new FlowControlSettings(options.MaxOutstandingMessages, options.MaxOutstandingByteCount),
            MaxTotalAckExtension = options.MaxTotalAckExtension
        };
    }

    /// <summary>
    /// Run one streaming pull session: build the subscriber, pump messages until the listener is cancelled or the
    /// session faults, then shut the subscriber down within a bounded budget.
    /// </summary>
    protected async Task listenWithSubscriberAsync(SubscriberClientBuilder subscriberBuilder)
    {
        if (_clients.ConfigureSubscriberClientBuilder != null)
        {
            await _clients.ConfigureSubscriberClientBuilder(subscriberBuilder);
        }

        var subscriber = await subscriberBuilder.BuildAsync();

        // GH-4065: the cancellation registration used to fire off StopAsync() and drop the returned task on the
        // floor, so nothing ever observed its completion or its exceptions. Route both the registration and the
        // finally block through the same memoized task instead: cancellation still starts the shutdown promptly,
        // and the finally block awaits that very same shutdown rather than racing a second one.
        var gate = new object();
        Task? shutdown = null;

        Task shutDownOnceAsync()
        {
            lock (gate)
            {
                return shutdown ??= stopAndDisposeSubscriberAsync(subscriber);
            }
        }

        var ctRegistration = _cancellation.Token.Register(() => _ = shutDownOnceAsync());

        try
        {
            await subscriber.StartAsync(async (PubsubMessage message, CancellationToken cancel) =>
            {
                // GH-4052. Under NativeAck the callback is held open until every envelope this delivery
                // carried reaches a terminal, because the callback's return value IS the settlement -- Pub/Sub
                // exposes no per-message ack. Every other mode returns as soon as the envelopes are handed
                // over, exactly as before.
                if (holdsUntilSettled)
                {
                    return await handleMessageHeldAsync(message);
                }

                var success = await handleMessageAsync(message);
                return success ? SubscriberClient.Reply.Ack : SubscriberClient.Reply.Nack;
            });
        }
        finally
        {
            ctRegistration.Unregister();
            await shutDownOnceAsync();
        }
    }

    private Task stopAndDisposeSubscriberAsync(SubscriberClient subscriber)
    {
        return StopAndDisposeSubscriberAsync(subscriber, _runtime.DurabilitySettings.DrainTimeout, _logger,
            _endpoint.Uri);
    }

    /// <summary>
    /// GH-4065. Shut a <see cref="SubscriberClient" /> down inside a bounded budget.
    /// <para>
    /// <c>SubscriberClient.StopAsync</c> waits on in-flight message callbacks, and our callback awaits
    /// <c>IReceiver.ReceivedAsync</c> -- which on an <c>EndpointMode.Inline</c> endpoint runs the whole handler
    /// pipeline. Passing <c>CancellationToken.None</c>, as this used to, therefore means a single slow handler
    /// wedges listener teardown with nothing at all bounding the wait.
    /// </para>
    /// <para>
    /// The budget is <see cref="DurabilitySettings.DrainTimeout" />, which is what
    /// <c>BufferedReceiver.DrainAsync</c> and <c>InlineReceiver.DrainAsync</c> already use for exactly this
    /// decision. Two nested deadlines are needed because <c>StopAsync(TimeSpan)</c> is only a <em>soft</em>
    /// stop: at its timeout it cancels each callback's <see cref="CancellationToken" /> and then keeps waiting
    /// for those callbacks to return, so a callback that never observes cancellation would still hang forever.
    /// Half the budget is spent letting callbacks finish on their own; the outer wait over the full budget is
    /// the hard stop that guarantees this method returns. Disposal re-enters the same wait inside the SDK and
    /// so gets a bound of its own.
    /// </para>
    /// <para>
    /// Anything still unacked when we give up is simply redelivered by Pub/Sub. Preferring "stop within the
    /// drain budget and let the rest be redelivered" over "wait forever" is the same trade every other
    /// Wolverine transport makes.
    /// </para>
    /// </summary>
    internal static async Task StopAndDisposeSubscriberAsync(SubscriberClient subscriber, TimeSpan drainTimeout,
        ILogger logger, Uri uri)
    {
        var softStop = TimeSpan.FromTicks(Math.Max(drainTimeout.Ticks / 2, 0));

        try
        {
            await subscriber.StopAsync(softStop).WaitAsync(drainTimeout);
        }
        catch (OperationCanceledException)
        {
            // The soft stop elapsed, the SDK cancelled the in-flight callbacks, and they unwound. Normal.
            logger.LogInformation(
                "{Uri}: Google Cloud Platform Pub/Sub listener did not finish its in-flight messages within the drain timeout of {DrainTimeout}; unacknowledged messages will be redelivered.",
                uri, drainTimeout);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "{Uri}: Google Cloud Platform Pub/Sub listener could not be stopped within the drain timeout of {DrainTimeout} because at least one message callback never returned. Abandoning the wait; unacknowledged messages will be redelivered.",
                uri, drainTimeout);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "{Uri}: Error while stopping the Google Cloud Platform Pub/Sub subscriber.", uri);
        }

        try
        {
            await subscriber.DisposeAsync().AsTask().WaitAsync(softStop);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "{Uri}: Error while disposing the Google Cloud Platform Pub/Sub subscriber.", uri);
        }
    }

    protected async Task<bool> handleMessageAsync(PubsubMessage message)
    {
        // GH-4066. The subscriber callback is held for as long as this takes, and on an EndpointMode.Inline
        // endpoint that is the entire handler pipeline. Outliving the ack extension budget means Pub/Sub starts
        // redelivering into a concurrent second execution, silently, so watch the clock on every delivery.
        var ticket = _ackExtensionWatchdog.Track(message.MessageId);

        try
        {
            return await processMessageAsync(message);
        }
        finally
        {
            _ackExtensionWatchdog.Release(ticket);
        }
    }

    /// <summary>
    /// GH-4052. The NativeAck path: map the delivery's envelopes, stamp them with the delivery key, hand them
    /// to the receiver, and then <b>wait</b> for the settle calls that the handler pipeline will make.
    /// </summary>
    private async Task<SubscriberClient.Reply> handleMessageHeldAsync(PubsubMessage message)
    {
        // Same watchdog as every other mode. It matters MORE here: the hold now spans lane queue time plus
        // handler time, and outliving the ack extension budget means Pub/Sub silently starts a concurrent
        // second execution rather than reporting anything (GH-4066, spike section 3).
        var ticket = _ackExtensionWatchdog.Track(message.MessageId);

        Envelope[] envelopes;

        try
        {
            envelopes = readEnvelopes(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Uri}: Error while mapping Google Cloud Platform Pub/Sub message {MessageId}.",
                _endpoint.Uri, message.MessageId);

            _ackExtensionWatchdog.Release(ticket);
            return SubscriberClient.Reply.Nack;
        }

        if (envelopes.Length == 0)
        {
            // Nothing to wait on. Ack rather than Nack: an empty delivery is not a failure, and nacking it
            // would have Pub/Sub redeliver something nobody will ever process
            _ackExtensionWatchdog.Release(ticket);
            return SubscriberClient.Reply.Ack;
        }

        var key = message.MessageId.IsNotEmpty() ? message.MessageId : Guid.NewGuid().ToString();
        var delivery = _held.Hold(key, envelopes.Length);

        foreach (var envelope in envelopes)
        {
            envelope.Headers[PubsubHeldDeliveries.DeliveryKeyHeader] = key;
        }

        try
        {
            await _receiver.ReceivedAsync(this, envelopes);

            return await delivery.Reply;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Uri}: Error while handing Pub/Sub message {MessageId} to the receiver.",
                _endpoint.Uri, message.MessageId);

            delivery.NackNow();
            return SubscriberClient.Reply.Nack;
        }
        finally
        {
            _held.Release(key);
            _ackExtensionWatchdog.Release(ticket);
        }
    }

    /// <summary>The envelopes one Pub/Sub delivery carries -- many when the sender batched them.</summary>
    private Envelope[] readEnvelopes(PubsubMessage message)
    {
        if (message.Attributes.Keys.Contains("batched"))
        {
            return EnvelopeSerializer.ReadMany(message.Data.ToByteArray()).ToArray();
        }

        var envelope = new Envelope();
        _mapper.MapIncomingToEnvelope(envelope, message);
        return [envelope];
    }

    private async Task<bool> processMessageAsync(PubsubMessage message)
    {
        if (message.Attributes.Keys.Contains("batched"))
        {
            var batched = EnvelopeSerializer.ReadMany(message.Data.ToByteArray());

            if (batched.Any())
            {
                await _receiver.ReceivedAsync(this, batched);
            }

            return true;
        }

        try
        {
            var envelope = new Envelope();

            _mapper.MapIncomingToEnvelope(envelope, message);

            await _receiver.ReceivedAsync(this, [envelope]);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Uri}: Error while mapping Google Cloud Platform Pub/Sub message {MessageId}.", _endpoint.Uri, message.MessageId);
            return false;
        }
    }

}