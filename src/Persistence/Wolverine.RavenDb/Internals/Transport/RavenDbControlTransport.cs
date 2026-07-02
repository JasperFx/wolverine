using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Transport;

internal class RavenDbControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "ravencontrol";

    private readonly Cache<Guid, RavenDbControlEndpoint> _endpoints;
    private readonly WolverineOptions _options;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    public RavenDbControlTransport(IDocumentStore store, WolverineOptions options)
    {
        Store = store;
        _options = options;

        _endpoints = new Cache<Guid, RavenDbControlEndpoint>(nodeId =>
        {
            return new RavenDbControlEndpoint(this, nodeId);
        });

        ControlEndpoint = _endpoints[_options.UniqueNodeId];
        ControlEndpoint.IsListener = true;
    }

    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    public RavenDbControlEndpoint ControlEndpoint { get; }

    public IDocumentStore Store { get; }

    public WolverineOptions Options => _options;

    public async ValueTask DisposeAsync()
    {
        if (_deleteBlock != null)
        {
            try
            {
                await _deleteBlock.DrainAsync();
            }
            catch (TaskCanceledException)
            {
            }

            _deleteBlock.SafeDispose();
        }
    }

    public string Protocol => ProtocolName;
    public string Name => "RavenDb Control Message Transport for Wolverine Control Messages";

    public Endpoint ReplyEndpoint()
    {
        return _endpoints[_options.UniqueNodeId];
    }

    public Endpoint GetOrCreateEndpoint(Uri uri)
    {
        var nodeId = Guid.Parse(uri.Host);
        return _endpoints[nodeId];
    }

    public Endpoint? TryGetEndpoint(Uri uri)
    {
        var nodeId = Guid.Parse(uri.Host);
        return _endpoints.TryFind(nodeId, out var e) ? e : null;
    }

    public IEnumerable<Endpoint> Endpoints()
    {
        return _endpoints;
    }

    public ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints()) endpoint.Compile(runtime);

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteEnvelopesAsync,
            runtime.LoggerFactory.CreateLogger<RavenDbControlTransport>(), runtime.Options.Durability.Cancellation);
        return ValueTask.CompletedTask;
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    public Task DeleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (_deleteBlock == null)
        {
            throw new InvalidOperationException("The RavenDbControlTransport has not been initialized");
        }

        return _deleteBlock.PostAsync(envelopes);
    }

    private async Task deleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        using var session = Store.OpenAsyncSession();
        foreach (var envelope in envelopes)
        {
            session.Delete(ControlMessage.IdFor(envelope.Id));
        }

        await session.SaveChangesAsync(cancellationToken);
    }
}
