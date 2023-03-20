using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using NpgsqlTypes;
using Wolverine.Transports.Postgresql.Internal;
using Xunit;

namespace Wolverine.Transports.Postgresql;

public class PostgresQueueListenerTests : IClassFixture<ExtendedPostgresResource>
{
    private readonly ExtendedPostgresResource _resource;
    private PostgresQueue _queue = null!;
    private PostgresQueueListener _listener = null!;

    public PostgresQueueListenerTests(ExtendedPostgresResource resource)
    {
        _resource = resource;
    }

    [Fact]
    public async Task ReadNext_WithMessageInQueue_ReturnsMessage()
    {
        // Arrange
        await Setup();
        var message = CreateMessage();
        await EnqueueMessage(message);

        // Act
        var result = _listener.ReadNext(CancellationToken.None);

        // Assert
        await Task.WhenAny(
            result.AsTask(),
            Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(result.IsCompletedSuccessfully,
            "Read next should return a completed task when there is a message in the queue");
    }

    [Fact]
    public async Task ReadNext_WithEmptyQueue_BlockUntilMessageInTheQueue()
    {
        // Arrange
        await Setup();

        // Act
        var readNextTask = _listener.ReadNext(CancellationToken.None);
        var stateBefore = readNextTask.IsCompleted;
        await EnqueueMessage(CreateMessage());
        for (var i = 0; i < 10 && !readNextTask.IsCompleted; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        // Assert
        Assert.False(stateBefore);
        Assert.True(
            readNextTask.IsCompletedSuccessfully,
            "Read next should return a completed task when there is a message in the queue");
    }

    [Fact]
    public async Task CompleteAsync_WithValidMessageId_NoMoreMessagesInTheDatabase()
    {
        // Arrange
        await Setup();
        var message = CreateMessage();
        await EnqueueMessage(message);
        var dequeuedMessage = await _listener.ReadNext(CancellationToken.None);

        // Act
        await _listener.CompleteAsync(dequeuedMessage!.Id, CancellationToken.None);

        // Assert
        var messagesInDatabase = await ReadAllMessagesFromTheDatabase();
        Assert.Empty(messagesInDatabase);
    }

    [Fact]
    public async Task DeferAsync_WithValidMessageId_MessageIsStillInTheDatabaseWithAttemptsValue2()
    {
        // Arrange
        await Setup();
        var message = CreateMessage();
        await EnqueueMessage(message);
        var dequeuedMessage = await _listener.ReadNext(CancellationToken.None);

        // Act
        await _listener.DeferAsync(dequeuedMessage!.Id, CancellationToken.None);

        // Assert
        var messagesInDatabase = await ReadAllMessagesFromTheDatabase();
        Assert.Single(messagesInDatabase);
        Assert.Equal(dequeuedMessage.Id, messagesInDatabase[0].Id);
        Assert.Equal(2, messagesInDatabase[0].Attempts);
    }

    [Fact]
    public async Task ReadNext_Should_ReadAllQueuedMessages_BeforeWaiting()
    {
        // arrange
        await Setup();
        var messages = Enumerable.Range(0, 5).Select(_ => CreateMessage()).ToList();
        foreach (var message in messages)
        {
            await EnqueueMessage(message);
        }

        // Act
        var readNextTasks = new List<Task<PostgresMessage>>();
        for (var i = 0; i < 5; i++)
        {
            readNextTasks.Add(_listener.ReadNext(CancellationToken.None).AsTask());
        }

        var allTask = Task.WhenAll(readNextTasks);

        // Assert
        await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(allTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReadNext_OnlyReadsMaxMessageCountThenWaits()
    {
        await Setup();
        var messages = new Queue<PostgresMessage>();
        for (var i = 0; i < 50; i++)
        {
            var message = CreateMessage();
            await EnqueueMessage(message);
        }

        // Read 20 messages
        for (var i = 0; i < 20; i++)
        {
            var message = await _listener.ReadNext(CancellationToken.None);
            messages.Enqueue(message!);
        }

        // the next two reads should be blocked
        var blockingRead1 = _listener.ReadNext(CancellationToken.None);
        var blockingRead2 = _listener.ReadNext(CancellationToken.None);
        await Task.WhenAny(
            blockingRead1.AsTask(),
            blockingRead2.AsTask(),
            Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.False(blockingRead1.IsCompleted, "Should block until further reads");
        Assert.False(blockingRead2.IsCompleted, "Should block until further reads");

        // after we complete one message, a new one should be read and the second one should
        // still be blocked
        await _listener.CompleteAsync(messages.Dequeue().Id, CancellationToken.None);
        await Task.WhenAny(
            blockingRead1.AsTask(),
            blockingRead2.AsTask(),
            Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.True(blockingRead1.IsCompleted, "This read should now be unblocked");
        Assert.False(blockingRead2.IsCompleted, "Should block until further reads");

        // after we complete the second message the second one should also be unblocked
        await _listener.CompleteAsync(messages.Dequeue().Id, CancellationToken.None);
        await Task.WhenAny(
            blockingRead2.AsTask(),
            Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.True(blockingRead2.IsCompleted, "This read should now be unblocked");

        // complete
        await _listener.CompleteAsync((await blockingRead1)!.Id, CancellationToken.None);
        await _listener.CompleteAsync((await blockingRead2)!.Id, CancellationToken.None);

        for (var i = 0; i < 28; i++)
        {
            await _listener.CompleteAsync(messages.Dequeue().Id, CancellationToken.None);
            var message = await _listener.ReadNext(CancellationToken.None);
            messages.Enqueue(message!);
        }

        while (messages.Count > 0)
        {
            await _listener.CompleteAsync(messages.Dequeue().Id, CancellationToken.None);
        }

        var left = await ReadAllMessagesFromTheDatabase();
        Assert.Empty(left);
    }

    [Fact]
    public async Task ReadNext_Should_RowLockTheMessage()
    {
        // arrange
        await Setup();
        var message = CreateMessage();
        await EnqueueMessage(message);

        // Act
        await _listener.ReadNext(CancellationToken.None);

        // Assert
        Assert.Single(await ReadAllMessagesFromTheDatabase());
        Assert.Empty(await ReadAllMessagesFromTheDatabaseSkipLocked());
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

    private async Task<List<PostMessageResult>> ReadAllMessagesFromTheDatabaseSkipLocked()
    {
        await using var connection =
            await _queue.Transport.Client.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT Id, Attempts
            FROM {_queue.QueueName}
            ORDER BY ScheduledTime ASC
            FOR UPDATE SKIP LOCKED
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

    [MemberNotNull(nameof(_queue), nameof(_listener))]
    private async Task Setup()
    {
        _queue = new PostgresQueue(
            new PostgresTransport
            {
                ConnectionString = await _resource.GetConnectionStringAsync()
            },
            new QueueDefinition("test_queue"));
        await _queue.SetupAsync(null!);
        _listener = new PostgresQueueListener(_queue);
    }
}
