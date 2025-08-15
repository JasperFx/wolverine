using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "dbcontrol";


    private readonly Cache<Guid, DatabaseControlEndpoint> _endpoints;

    private readonly WolverineOptions _options;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    // Only needs the main database. Switch to taking in a database poller later
    public DatabaseControlTransport(IMessageDatabase database, WolverineOptions options)
    {
        Database = database;
        _options = options;

        _endpoints = new Cache<Guid, DatabaseControlEndpoint>(nodeId =>
        {
            return new DatabaseControlEndpoint(this, nodeId);
        });

        ControlEndpoint = _endpoints[_options.UniqueNodeId];
        ControlEndpoint.IsListener = true;

        Options = options;

        TableName = new DbObjectName(database.SchemaName, DatabaseConstants.ControlQueueTableName);
    }

    public DatabaseControlEndpoint ControlEndpoint { get; }

    public IMessageDatabase Database { get; }

    public DbObjectName TableName { get; }

    public WolverineOptions Options { get; }

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
    public string Name => "Simple Database Control Message Transport for Wolverine Control Messages";

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
        foreach (var endpoint in Endpoints())
        {
            endpoint.Compile(runtime);
        }

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteEnvelopesAsync,
            runtime.LoggerFactory.CreateLogger<DatabaseControlTransport>(), runtime.Options.Durability.Cancellation);
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
            throw new InvalidOperationException("The DatabaseControlTransport has not been initialized");
        }

        return _deleteBlock.PostAsync(envelopes);
    }

    private async Task deleteEnvelopesAsync(List<Envelope> envelopes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Database.HasDisposed) return;

        await using var conn = await Database.DataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            if (envelopes.Count == 1)
            {
                await conn.CreateCommand($"delete from {TableName} where id = @id").With("id", envelopes.Single().Id)
                    .ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                var builder = Database.ToCommandBuilder();
                foreach (var envelope in envelopes)
                {
                    builder.Append("delete from ");
                    builder.Append(TableName.QualifiedName);
                    builder.Append(" where id = ");
                    builder.AppendParameter(envelope.Id);
                    builder.Append(';');
                }

                var command = builder.Compile();
                command.Connection = conn;

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}