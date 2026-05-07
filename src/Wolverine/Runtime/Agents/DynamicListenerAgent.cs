using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Cluster-coordinated agent that activates a single dynamically-registered listener
/// URI on whichever node the <see cref="DynamicListenerAgentFamily"/> assigns it to.
/// The persisted listener URI lives in <see cref="Persistence.Durability.IListenerStore"/>;
/// the agent simply resolves the URI to its transport via
/// <see cref="ITransportCollection.ForScheme"/> and delegates to
/// <see cref="IEndpointCollection.StartListenerAsync"/> /
/// <see cref="IEndpointCollection.StopListenerAsync"/> on Start/Stop.
///
/// Failure to resolve the transport is treated as a hard error rather than a
/// silent skip — this surfaces "the user registered an MQTT URI but the host
/// doesn't have <c>UseMqtt</c>" misconfigurations early instead of letting the
/// listener silently never run.
/// </summary>
internal sealed class DynamicListenerAgent : IAgent
{
    private readonly IWolverineRuntime _runtime;
    private readonly Uri _listenerUri;
    private Endpoint? _endpoint;

    public DynamicListenerAgent(IWolverineRuntime runtime, Uri listenerUri)
    {
        _runtime = runtime;
        _listenerUri = listenerUri;
        Uri = DynamicListenerUriEncoding.ToAgentUri(listenerUri);
    }

    public Uri Uri { get; }

    public AgentStatus Status { get; set; } = AgentStatus.Running;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _endpoint ??= resolveEndpoint();
        await _runtime.Endpoints.StartListenerAsync(_endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is null)
        {
            // Never started — nothing to do.
            Status = AgentStatus.Stopped;
            return;
        }

        await _runtime.Endpoints.StopListenerAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        Status = AgentStatus.Stopped;
    }

    public string Description =>
        $"Dynamic listener for {_listenerUri} — registered at runtime via " +
        $"{nameof(Persistence.Durability.IListenerStore)}, activated on this node by the cluster.";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (Status != AgentStatus.Running)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Agent {Uri} is {Status}"));
        }

        if (_endpoint is null)
        {
            // We're "Running" but have never been started — the agent runtime
            // calls StartAsync before health checks ever fire, so reaching this
            // branch means something else is wrong.
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Dynamic listener {_listenerUri} has not yet been started"));
        }

        var listeningAgent = _runtime.Endpoints.FindListeningAgent(_endpoint.Uri);
        if (listeningAgent is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy());
        }

        return Task.FromResult(listeningAgent.Status switch
        {
            ListeningStatus.TooBusy => HealthCheckResult.Degraded(
                $"Dynamic listener {_listenerUri} is too busy"),
            ListeningStatus.GloballyLatched => HealthCheckResult.Unhealthy(
                $"Dynamic listener {_listenerUri} is globally latched"),
            _ => HealthCheckResult.Healthy()
        });
    }

    private Endpoint resolveEndpoint()
    {
        var transport = _runtime.Options.Transports.ForScheme(_listenerUri.Scheme);
        if (transport is null)
        {
            throw new InvalidOperationException(
                $"No registered transport supports scheme '{_listenerUri.Scheme}' — " +
                $"the dynamic listener URI '{_listenerUri}' cannot be activated. " +
                $"Did the host's WolverineOptions register the relevant transport (e.g. UseMqtt)?");
        }

        var endpoint = transport.GetOrCreateEndpoint(_listenerUri);
        endpoint.IsListener = true;
        return endpoint;
    }
}
