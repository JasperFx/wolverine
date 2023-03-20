using Npgsql;
using NpgsqlTypes;
using Wolverine.Configuration;
using Wolverine.Transports.Postgresql.Internal;
using Xunit;

namespace Wolverine.Transports.Postgresql;

public class QueueDefinitionIntegrationTests
    : IClassFixture<ExtendedPostgresResource>
{
    private readonly ExtendedPostgresResource _resource;

    public QueueDefinitionIntegrationTests(ExtendedPostgresResource resource)
    {
        _resource = resource;
    }

    [Fact]
    public async Task CreateAsync_QueueIsCreated()
    {
        // Arrange
        var connection = await SetupConnection();
        var queueDefinition = new QueueDefinition("TestQueue");

        // Act
        await queueDefinition.CreateAsync(connection, CancellationToken.None);
        bool exists = await queueDefinition.ExistsAsync(connection, CancellationToken.None);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_QueueExists_ReturnsTrue()
    {
        // Arrange
        var connection = await SetupConnection();
        var queueDefinition = new QueueDefinition("TestQueue");
        await queueDefinition.CreateAsync(connection, CancellationToken.None);

        // Act
        bool exists = await queueDefinition.ExistsAsync(connection, CancellationToken.None);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsAsync_QueueDoesNotExist_ReturnsFalse()
    {
        // Arrange
        var connection = await SetupConnection();
        var queueDefinition = new QueueDefinition("TestQueue");

        // Act
        bool exists = await queueDefinition.ExistsAsync(connection, CancellationToken.None);

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task DropAsync_QueueIsDropped()
    {
        // Arrange
        var connection = await SetupConnection();
        var queueDefinition = new QueueDefinition("TestQueue");
        await queueDefinition.CreateAsync(connection, CancellationToken.None);

        // Act
        await queueDefinition.DropAsync(connection, CancellationToken.None);
        bool exists = await queueDefinition.ExistsAsync(connection, CancellationToken.None);

        // Assert
        Assert.False(exists);
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