using System.Data;
using Npgsql;
using Wolverine.Transports.Postgresql.Internal;
using Xunit;

namespace Wolverine.Transports.Postgresql;

public class PostgresChannelWaitHandleIntegrationTests
    : IClassFixture<ExtendedPostgresResource>
{
    private readonly ExtendedPostgresResource _resource;

    public PostgresChannelWaitHandleIntegrationTests(ExtendedPostgresResource resource)
    {
        _resource = resource;
    }

    [Fact]
    public async Task Should_OpenConnection_When_Created()
    {
        // Arrange
        var connection = await SetupConnection();
        string channelName = "test_channel";
        var cancellationToken = new CancellationToken();

        // Act
        var waitHandle = await PostgresChannelWaitHandle.CreateAsync(
            _ => new ValueTask<NpgsqlConnection>(connection),
            channelName,
            cancellationToken);

        // Assert
        Assert.NotNull(waitHandle);
        Assert.IsType<PostgresChannelWaitHandle>(waitHandle);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Should_DisposeConnection_When_DisposeAsyncCalledOnValidInstance()
    {
        // Arrange
        var connection = await SetupConnection();
        string channelName = "test_channel";
        var cancellationToken = new CancellationToken();

        var waitHandle = await PostgresChannelWaitHandle.CreateAsync(
            _ => new ValueTask<NpgsqlConnection>(connection),
            channelName,
            cancellationToken);

        // Act
        await waitHandle.DisposeAsync();

        // Assert
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Should_ReceiveNotification_When_NotifyCommandIsExecuted()
    {
        // Arrange
        string channelName = "test_channel";
        var dbName = await _resource.GetConnectionStringAsync();
        var cancellationToken = new CancellationToken();

        var waitHandle = await PostgresChannelWaitHandle.CreateAsync(
            async _ => await SetupConnection(dbName),
            channelName,
            cancellationToken);

        var notificationTask = waitHandle.WaitOneAsync(cancellationToken);

        using var notifyingConnection = await SetupConnection(dbName);

        // Act
        await using var command = notifyingConnection.CreateCommand();
        command.CommandText = $"NOTIFY {channelName};";
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Assert
        await Task.WhenAny(
            notificationTask,
            Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
        Assert.True(notificationTask.IsCompleted,
            "Notification not received within the expected time");
    }

    private async Task<NpgsqlConnection> SetupConnection(string? connectionString = null)
    {
        connectionString ??= await _resource.GetConnectionStringAsync();
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var connection = dataSource.CreateConnection();
        await connection.OpenAsync();
        return connection;
    }
}