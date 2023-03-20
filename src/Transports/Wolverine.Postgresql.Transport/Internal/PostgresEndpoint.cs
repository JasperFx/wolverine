using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Transports.Postgresql.Internal;

public abstract class PostgresEndpoint : Endpoint, IBrokerEndpoint, IPostgresListeningEndpoint
{
    private readonly DatabaseObjectDefinition _definition;

    public PostgresEndpoint(
        PostgresTransport transport,
        EndpointRole role,
        Uri uri,
        DatabaseObjectDefinition definition)
        : base(uri, role)
    {
        Transport = transport;
        _definition = definition;
    }

    public PostgresTransport Transport { get; }

    /// <summary>
    ///    The maximum number of messages that can be processed concurrently by the listener.
    /// </summary>
    public int MaximumConcurrentMessages { get; internal set; } = 20;

    public virtual async ValueTask<bool> CheckAsync()
    {
        return await Transport.Client.ExistsAsync(_definition, default);
    }

    public virtual async ValueTask TeardownAsync(ILogger logger)
    {
        await Transport.Client.DropAsync(_definition, default);
    }

    public virtual async ValueTask SetupAsync(ILogger logger)
    {
        var exists = await CheckAsync();
        if (!exists)
        {
            await Transport.Client.CreateAsync(_definition, default);
        }
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Inline;
    }

    internal IEnvelopeMapper<PostgresMessage, PostgresMessage> BuildMapper(
        IWolverineRuntime runtime)
    {
        var mapper = new PostgresEnvelopeMapper(this, runtime);

        return mapper;
    }
}
