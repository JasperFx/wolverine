using Npgsql;
using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

internal sealed class PostgresClient : IAsyncDisposable
{
    public PostgresClient(string connectionString)
    {
        DataSource = NpgsqlDataSource.Create(connectionString);
    }

    public NpgsqlDataSource DataSource { get; }

    public async Task<PostgresChannelWaitHandle> GetChannelWaitHandleAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var waitHandle = await PostgresChannelWaitHandle
            .CreateAsync(ct => DataSource.OpenConnectionAsync(ct), name, cancellationToken);

        return waitHandle;
    }

    public ValueTask DisposeAsync()
    {
        return DataSource.DisposeAsync();
    }
}