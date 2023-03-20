using System.Collections.Immutable;
using Castle.Core.Logging;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NpgsqlTypes;
using Wolverine.Runtime;
using Wolverine.Transports.Postgresql.Internal;
using Xunit;

namespace Wolverine.Transports.Postgresql;

public class PostgresQueueTests : IClassFixture<ExtendedPostgresResource>
{
    private readonly ExtendedPostgresResource _resource;
    private PostgresQueue _queue;

    public PostgresQueueTests(ExtendedPostgresResource resource)
    {
        _resource = resource;
    }

    [Fact]
    public async Task InitializeAsync_WithAutoProvision_ShouldSetupQueue()
    {
        // Arrange
        _queue = await CreatePostgresQueue(autoProvision: true);

        // Act
        await _queue.InitializeAsync(null!);

        // Assert
        Assert.True(await _queue.CheckAsync());
    }

    [Fact]
    public async Task InitializeAsync_WithAutoPurge_ShouldPurgeQueue()
    {
        // Arrange
        _queue = await CreatePostgresQueue(autoPurge: true);
        await _queue.SetupAsync(null!);
        await EnqueueMessage(CreateMessage());

        // Act
        await _queue.InitializeAsync(null!);

        // Assert
        var messagesInDatabase = await ReadAllMessagesFromTheDatabase();
        Assert.Empty(messagesInDatabase);
    }

    [Fact]
    public async Task PurgeAsync_WithMessagesInQueue_ShouldRemoveAllMessages()
    {
        // Arrange
        _queue = await CreatePostgresQueue();
        await _queue.SetupAsync(null!);
        await EnqueueMessage(CreateMessage());

        // Act
        await _queue.PurgeAsync(null!);

        // Assert
        var messagesInDatabase = await ReadAllMessagesFromTheDatabase();
        Assert.Empty(messagesInDatabase);
    }

    [Fact]
    public async Task GetAttributesAsync_ShouldReturnQueueName()
    {
        // Arrange
        _queue = await CreatePostgresQueue();

        // Act
        var attributes = await _queue.GetAttributesAsync();

        // Assert
        Assert.Equal(_queue.QueueName, attributes["Name"]);
    }

    [Fact]
    public async Task BuildListenerAsync_Should_CreateListenerThatCanReadMessages()
    {
        // Arrange
        _queue = await CreatePostgresQueue();
        await _queue.SetupAsync(null!);
        var runtime = new Mock<IWolverineRuntime>();
        runtime
            .SetupGet(x => x.Logger)
            .Returns(() => new Mock<Microsoft.Extensions.Logging.ILogger>().Object);
        var receiver = new Mock<IReceiver>();

        Envelope? receivedEnvelope = null;
        receiver
            .Setup(x => x.ReceivedAsync(It.IsAny<IListener>(), It.IsAny<Envelope>()))
            .Callback((IListener _, Envelope envelope) => { receivedEnvelope = envelope; })
            .Returns(() => new ValueTask());

        // Act
        await _queue.BuildListenerAsync(runtime.Object, receiver.Object);
        var message = CreateMessage();
        await EnqueueMessage(message);

        // Assert
        var result = WaitUntilEmpty();
        await Task.WhenAny(result, Task.Delay(5.Hours()));

        Assert.NotNull(receivedEnvelope);

        async Task WaitUntilEmpty()
        {
            while (receivedEnvelope is null)
            {
                await Task.Delay(100);
            }
        }
    }
    
    [Fact]
    public async Task CheckAsync_ReturnsTrue_WhenQueueExists()
    {
        // Arrange
        var queue = await CreatePostgresQueue();

        // Act
        var exists = await queue.CheckAsync();

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFalse_WhenQueueDoesNotExist()
    {
        // Arrange
        var queue = await CreatePostgresQueue();
        await queue.TeardownAsync(new NullLogger<PostgresQueue>());

        // Act
        var exists = await queue.CheckAsync();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task TeardownAsync_RemovesQueue()
    {
        // Arrange
        var queue = await CreatePostgresQueue();

        // Act
        await queue.TeardownAsync(new NullLogger<PostgresQueue>());
        var exists = await queue.CheckAsync();

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task SetupAsync_CreatesQueue()
    {
        // Arrange
        var queue = await CreatePostgresQueue();
        await queue.TeardownAsync(new NullLogger<PostgresQueue>());

        // Act
        await queue.SetupAsync(new NullLogger<PostgresQueue>());
        var exists = await queue.CheckAsync();

        // Assert
        Assert.True(exists);
    }

    private async Task<PostgresQueue> CreatePostgresQueue(
        bool autoProvision = false,
        bool autoPurge = false)
    {
        _queue = new PostgresQueue(
            new PostgresTransport
            {
                ConnectionString = await _resource.GetConnectionStringAsync(),
                AutoProvision = autoProvision,
                AutoPurgeAllQueues = autoPurge
            },
            new QueueDefinition("test_queue"));

        await _queue.SetupAsync(null!);
        return _queue;
    }

    private async Task EnqueueMessage(PostgresMessage message)
    {
        await using var connection =
            await _queue.Transport.Client.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            INSERT INTO {_queue.QueueName} (Id, SenderId, CorrelationId, MessageType, ContentType, ScheduledTime, Data, Headers, Attempts)
            VALUES (@id, @senderId, @correlationId, @messageType, @contentType, @scheduledTime, @data, @header, @attempt);
        """;

        // add the parameters to the command
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, message.Id);
        command.Parameters.AddWithValue("senderId", NpgsqlDbType.Uuid, Guid.NewGuid());
        command.Parameters.AddWithValue("correlationId",
            NpgsqlDbType.Varchar,
            message.CorrelationId!);
        command.Parameters.AddWithValue("messageType", NpgsqlDbType.Varchar, message.MessageType!);
        command.Parameters.AddWithValue("contentType", NpgsqlDbType.Varchar, message.ContentType!);
        command.Parameters.AddWithValue("scheduledTime",
            NpgsqlDbType.TimestampTz,
            message.ScheduledTime);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Bytea, message.Data!);
        command.Parameters.AddWithValue("header",
            NpgsqlDbType.Jsonb,
            (IReadOnlyDictionary<string, string>?) message.Headers ??
            ImmutableDictionary<string, string>.Empty);
        command.Parameters.AddWithValue("attempt", message.Attempts);

        // execute the command
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<PostMessageResult>> ReadAllMessagesFromTheDatabase()
    {
        await using var connection =
            await _queue.Transport.Client.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT Id, Attempts
            FROM {_queue.QueueName}
            ORDER BY ScheduledTime ASC
            LIMIT 1;
        """;

        var results = new List<PostMessageResult>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetFieldValue<Guid>(0);
            var attempts = reader.GetFieldValue<int>(1);
            var message = new PostMessageResult(id, attempts);
            results.Add(message);
        }

        return results;
    }

    private PostgresMessage CreateMessage()
    {
        var message = new PostgresMessage
        {
            Id = Guid.NewGuid(),
            CorrelationId = "CorrelationId",
            MessageType = "MessageType",
            ContentType = "ContentType",
            ScheduledTime = DateTimeOffset.UtcNow,
            Data = Array.Empty<byte>(),
            Headers = new Dictionary<string, string>(),
            Attempts = 1
        };
        return message;
    }

    private record PostMessageResult(Guid Id, int Attempts);
}
