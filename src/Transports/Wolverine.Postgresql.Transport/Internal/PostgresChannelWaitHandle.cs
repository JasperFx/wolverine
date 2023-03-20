using Npgsql;

namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresChannelWaitHandle : IAsyncDisposable
{
    private readonly string _channelName;
    private readonly NpgsqlConnection _connection;

    private PostgresChannelWaitHandle(string channelName, NpgsqlConnection connection)
    {
        _channelName = channelName;
        _connection = connection;
    }

    public Task WaitOneAsync(CancellationToken cancellationToken)
    {
        return _connection.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        // sql inecjtion
        command.CommandText = $"LISTEN {_channelName}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<PostgresChannelWaitHandle> CreateAsync(
        Func<CancellationToken, ValueTask<NpgsqlConnection>> connectionFactory,
        string channelName,
        CancellationToken cancellationToken)
    {
        var connection = await connectionFactory(cancellationToken);

        var channel = new PostgresChannelWaitHandle(channelName, connection);

        await channel.Initialize(cancellationToken);

        return channel;
    }
}
