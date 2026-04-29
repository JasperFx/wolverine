using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Oracle;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Oracle.Transport;

/// <summary>
/// Oracle-specific clone of <see cref="Wolverine.RDBMS.Transport.DatabaseControlTransport"/>.
/// The shared RDBMS implementation assumes <c>@param</c> placeholders and Guid values that map
/// directly onto a DbParameter — neither is true on Oracle, where placeholders use the
/// <c>:param</c> syntax and the control queue's id columns are stored as RAW(16) requiring
/// byte[]. Rather than generalize the shared implementation, we run a parallel one here.
/// See #2622.
/// </summary>
internal class OracleControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "oraclecontrol";

    private readonly Cache<Guid, OracleControlEndpoint> _endpoints;
    private readonly WolverineOptions _options;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    public OracleControlTransport(OracleMessageStore database, WolverineOptions options)
    {
        Database = database;
        _options = options;

        _endpoints = new Cache<Guid, OracleControlEndpoint>(nodeId => new OracleControlEndpoint(this, nodeId));

        ControlEndpoint = _endpoints[_options.UniqueNodeId];
        ControlEndpoint.IsListener = true;

        Options = options;
        TableName = new DbObjectName(database.SchemaName,
            DatabaseConstants.ControlQueueTableName.ToUpperInvariant());
    }

    public OracleControlEndpoint ControlEndpoint { get; }

    public OracleMessageStore Database { get; }

    public DbObjectName TableName { get; }

    public WolverineOptions Options { get; }

    public string Protocol => ProtocolName;
    public string Name => "Oracle-backed Control Message Transport for Wolverine Control Messages";

    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    public Endpoint ReplyEndpoint() => _endpoints[_options.UniqueNodeId];

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

    public IEnumerable<Endpoint> Endpoints() => _endpoints;

    public ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints()) endpoint.Compile(runtime);

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteEnvelopesAsync,
            runtime.LoggerFactory.CreateLogger<OracleControlTransport>(), runtime.Options.Durability.Cancellation);
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
            throw new InvalidOperationException("The OracleControlTransport has not been initialized");
        }

        return _deleteBlock.PostAsync(envelopes);
    }

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

    private async Task deleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Database.HasDisposed)
        {
            return;
        }

        await using var conn = await Database.OracleDataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            // Oracle has no reliable batch DELETE-by-id syntax in a single command, so we
            // serialize the deletes. Volume here is tiny (control messages already consumed).
            foreach (var envelope in envelopes)
            {
                await using var cmd = conn.CreateCommand($"DELETE FROM {TableName.QualifiedName} WHERE id = :id");
                cmd.With("id", envelope.Id);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
