using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.SignalR.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Client;

public class SignalRClientEndpoint : Endpoint, IListener, ISender
{
    private readonly SignalRClientTransport _parent;
    private HubConnection? _connection;
    private CloudEventsMapper? _mapper;
    private ILogger<SignalRClientEndpoint>? Logger;

    internal static Uri TranslateToWolverineUri(Uri uri)
    {
        return new Uri($"{SignalRClientTransport.ProtocolName}://{uri.Host}:{uri.Port}/{uri.Segments.Last()}");
    }

    public SignalRClientEndpoint(Uri uri, SignalRClientTransport parent) : base(TranslateToWolverineUri(uri), EndpointRole.Application)
    {
        _parent = parent;
        SignalRUri = uri;

        IsListener = true;
        BrokerRole = "hub";

        Mode = EndpointMode.Inline;

        // Just to use the same defaults
        JsonOptions = new SignalRTransport().JsonOptions;
    }

    [IgnoreDescription]
    public JsonSerializerOptions JsonOptions { get; set; }

    [DescribeAsConfigurationState]
    public Func<IServiceProvider, Func<Task<string?>>> AccessTokenProvider { get; set; } = null!;

    public Uri SignalRUri { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        Receiver = receiver;
        Pipeline = runtime.Pipeline;
        _connection ??= new HubConnectionBuilder()
            .WithAutomaticReconnect()
            .WithUrl(SignalRUri, opts =>
            {
                opts.AccessTokenProvider = AccessTokenProvider?.Invoke(runtime.Services);
            })
            .Build();
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);

        Logger = runtime.LoggerFactory.CreateLogger<SignalRClientEndpoint>();

        try
        {
            await _connection.StartAsync();
        }
        catch (HttpRequestException ex)
        {
            // GH-4519: a 401 here used to be logged and swallowed (the `//throw;` FIXME). WithAutomaticReconnect()
            // only engages after a *successful* initial connection, so the endpoint stayed dead forever and every
            // later send failed with "SignalR Client {Uri} is not initialized" -- a message that points at startup
            // ordering when the real cause is the access-token provider or the hub's authorization policy.
            // A 403, a TLS failure and a DNS failure were never in that filter and already propagated; the
            // asymmetry was the bug. Fail the host start with the real cause instead.
            Logger.LogError(ex, "Unable to connect to the SignalR hub at {Uri}", SignalRUri);
            throw new InvalidOperationException(ConnectFailureMessage(SignalRUri, ex.StatusCode), ex);
        }

        _connection.On(SignalRTransport.DefaultOperation, [typeof(string)], (args =>
        {
            var json = args[0] as string;

            if (json is null)
            {
                Logger.LogDebug("Received an empty message, ignoring");
                return Task.CompletedTask;
            }

            return ReceiveAsync(json);
        }));

        // GH-3972: the matching unwrap for a coalesced batch. Registered unconditionally -- the server
        // decides whether to coalesce, and a client that only listened for it when locally configured to
        // would silently drop every batch when the server turned coalescing on.
        _connection.On(SignalRTransport.CoalescedOperation, [typeof(string)], (args =>
        {
            var json = args[0] as string;

            if (json is null)
            {
                Logger.LogDebug("Received an empty coalesced batch, ignoring");
                return Task.CompletedTask;
            }

            return ReceiveCoalescedAsync(json);
        }));

        return this;
    }

    /// <summary>
    ///     GH-3972. Unwraps a coalesced batch and feeds each inner CloudEvents document through the normal
    ///     receive path, in the order the server accumulated them.
    /// </summary>
    internal async Task ReceiveCoalescedAsync(string json)
    {
        if (!CoalescedSignalRMessage.TryReadItems(json, JsonOptions, out var items))
        {
            Logger?.LogError(
                "Received a payload on {Operation} that could not be read as a coalesced batch, discarding it",
                SignalRTransport.CoalescedOperation);
            return;
        }

        foreach (var item in items)
        {
            // Sequentially and in order: a coalesced batch preserves arrival order, and fanning these out
            // concurrently would throw that away for no gain.
            await ReceiveAsync(item);
        }
    }

    internal async Task ReceiveAsync(string json)
    {
        if (Receiver == null || _mapper == null) return;

        if (json.IsEmpty())
        {
            Logger?.LogError(new ArgumentOutOfRangeException(nameof(json)), "Received empty json into the SignalR client");
        }

        try
        {
            var envelope = new Envelope();
            _mapper!.MapIncoming(envelope, json);
            await Receiver.ReceivedAsync(this, envelope);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Unable to receive a message from SignalR");
        }
    }

    [IgnoreDescription]
    public IReceiver? Receiver { get; private set; }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        _connection ??= new HubConnectionBuilder()
            .WithUrl(SignalRUri, opts =>
            {
                opts.AccessTokenProvider = AccessTokenProvider?.Invoke(runtime.Services);
            })
            .Build();
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        return this;
    }

    [IgnoreDescription]
    public IHandlerPipeline? Pipeline { get; private set; }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
        }

        _connection = null;
    }

    Uri IListener.Address => Uri;

    async ValueTask IListener.StopAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
        }
    }

    bool ISender.SupportsNativeScheduledSend => false;
    Uri ISender.Destination => Uri;

    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    /// <summary>
    /// GH-4519: the remedy depends on *why* the hub refused the connection, and 401/403 is far and away the
    /// most common cause in the wild, so name the two things that actually produce it.
    /// </summary>
    internal static string ConnectFailureMessage(Uri signalRUri, HttpStatusCode? statusCode)
    {
        var reason = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "the hub returned 401 Unauthorized. Check the AccessTokenProvider configured on this endpoint and the hub's authorization policy",
            HttpStatusCode.Forbidden =>
                "the hub returned 403 Forbidden. The access token was accepted but does not satisfy the hub's authorization policy",
            null => "the connection attempt failed before the hub responded. Check the Uri, DNS, and any TLS configuration",
            _ => $"the hub returned {(int)statusCode} {statusCode}"
        };

        return $"Wolverine could not connect to the SignalR hub at '{signalRUri}': {reason}. See the inner exception for the underlying HttpRequestException.";
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        if (_mapper == null || _connection == null)
            throw new InvalidOperationException(
                $"The SignalR client endpoint '{Uri}' is not initialized, so it cannot send. Either the Wolverine host has not been started yet, or the listener for this endpoint was never built (listeners are built during host startup).");

        var json = _mapper.WriteToString(envelope);

        await _connection.InvokeAsync(nameof(WolverineHub.ReceiveMessage), json);
    }
}