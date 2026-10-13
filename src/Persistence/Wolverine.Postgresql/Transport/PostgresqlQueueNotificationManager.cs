using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;

namespace Wolverine.Postgresql.Transport;

/// <summary>
/// Shares one PostgreSQL notification connection among every native queue listener using the same
/// data source. Notifications are only wakeups; the queue remains the source of truth.
/// </summary>
internal sealed class PostgresqlQueueNotificationManager
{
    private readonly object _lock = new();
    private readonly Dictionary<NpgsqlDataSource, DatabaseListener> _listeners = new(ReferenceEqualityComparer.Instance);
    private long _nextSubscriptionId;

    public async ValueTask<IAsyncDisposable> RegisterAsync(NpgsqlDataSource dataSource, string databaseName,
        string queueKey, Action wake, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DatabaseListener listener;
        long subscriptionId;
        lock (_lock)
        {
            if (!_listeners.TryGetValue(dataSource, out listener!))
            {
                if (new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Multiplexing)
                {
                    throw new InvalidOperationException(
                        "PostgreSQL queue notifications require a non-multiplexed NpgsqlDataSource because LISTEN needs a dedicated session. Set Multiplexing=false or disable queue notifications.");
                }

                var schemaName = queueKey.Split('.', 2)[0];
                listener = new DatabaseListener(dataSource, databaseName,
                    PostgresqlQueueNotification.ApplicationName(schemaName), logger);
                _listeners.Add(dataSource, listener);
            }

            subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
            listener.Register(queueKey, subscriptionId, wake);
            listener.Start();
        }

        try
        {
            // Do not report the listener as started until PostgreSQL has processed LISTEN. Otherwise a message
            // sent immediately after host startup could miss its wakeup and wait for the ordinary poll interval.
            // A failed first attempt is nonfatal: the manager keeps reconnecting and queue polling remains active.
            await listener.WaitForInitialListenAttemptAsync(cancellationToken);
            return new Registration(this, dataSource, listener, queueKey, subscriptionId);
        }
        catch
        {
            await unregisterAsync(dataSource, listener, queueKey, subscriptionId);
            throw;
        }
    }

    private async ValueTask unregisterAsync(NpgsqlDataSource dataSource, DatabaseListener listener, string queueKey,
        long subscriptionId)
    {
        DatabaseListener? stop = null;
        lock (_lock)
        {
            listener.Unregister(queueKey, subscriptionId);
            if (listener.IsEmpty && _listeners.TryGetValue(dataSource, out var current) && ReferenceEquals(current, listener))
            {
                _listeners.Remove(dataSource);
                stop = listener;
            }
        }

        if (stop != null)
        {
            await stop.DisposeAsync();
        }
    }

    private sealed class Registration(PostgresqlQueueNotificationManager owner, NpgsqlDataSource dataSource,
        DatabaseListener listener, string queueKey, long subscriptionId) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.unregisterAsync(dataSource, listener, queueKey, subscriptionId)
                : ValueTask.CompletedTask;
        }
    }

    private sealed class DatabaseListener : IAsyncDisposable
    {
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
        private const string ListenSql = "LISTEN wolverine_queue_wakeup";

        private readonly NpgsqlDataSource _dataSource;
        private readonly string _databaseName;
        private readonly string _applicationName;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<bool> _firstListenAttempt =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _subscriptionsLock = new();
        private readonly Dictionary<string, Dictionary<long, Action>> _subscriptions = new(StringComparer.Ordinal);
        private Task? _task;

        public DatabaseListener(NpgsqlDataSource dataSource, string databaseName, string applicationName,
            ILogger logger)
        {
            _dataSource = dataSource;
            _databaseName = databaseName;
            _applicationName = applicationName;
            _logger = logger;
        }

        public bool IsEmpty
        {
            get
            {
                lock (_subscriptionsLock)
                {
                    return _subscriptions.Count == 0;
                }
            }
        }

        public void Register(string queueKey, long subscriptionId, Action wake)
        {
            lock (_subscriptionsLock)
            {
                if (!_subscriptions.TryGetValue(queueKey, out var callbacks))
                {
                    callbacks = new Dictionary<long, Action>();
                    _subscriptions.Add(queueKey, callbacks);
                }

                callbacks.Add(subscriptionId, wake);
            }
        }

        public void Unregister(string queueKey, long subscriptionId)
        {
            lock (_subscriptionsLock)
            {
                if (!_subscriptions.TryGetValue(queueKey, out var callbacks)) return;

                callbacks.Remove(subscriptionId);
                if (callbacks.Count == 0)
                {
                    _subscriptions.Remove(queueKey);
                }
            }
        }

        public void Start()
        {
            _task ??= Task.Run(runAsync);
        }

        public Task<bool> WaitForInitialListenAttemptAsync(CancellationToken cancellationToken)
        {
            return _firstListenAttempt.Task.WaitAsync(cancellationToken);
        }

        private void onNotification(object sender, NpgsqlNotificationEventArgs args)
        {
            Action[] callbacks;
            lock (_subscriptionsLock)
            {
                callbacks = _subscriptions.TryGetValue(args.Payload, out var registered)
                    ? registered.Values.ToArray()
                    : [];
            }

            foreach (var callback in callbacks)
            {
                try
                {
                    callback();
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Could not signal a PostgreSQL queue listener after a notification for {Database}",
                        _databaseName);
                }
            }
        }

        private async Task runAsync()
        {
            var failures = 0;

            while (!_cancellation.IsCancellationRequested)
            {
                NpgsqlConnection? connection = null;
                try
                {
                    connection = await _dataSource.OpenConnectionAsync(_cancellation.Token);
                    connection.Notification += onNotification;

                    await using (var listen = connection.CreateCommand(ListenSql))
                    {
                        await listen.ExecuteNonQueryAsync(_cancellation.Token);
                    }

                    await using (var applicationName = connection
                                     .CreateCommand("select set_config('application_name', :name, false)")
                                     .With("name", _applicationName))
                    {
                        await applicationName.ExecuteScalarAsync(_cancellation.Token);
                    }

                    _firstListenAttempt.TrySetResult(true);

                    if (failures > 0)
                    {
                        _logger.LogInformation("PostgreSQL queue notifications reconnected for database {Database}",
                            _databaseName);
                    }

                    failures = 0;
                    while (!_cancellation.IsCancellationRequested)
                    {
                        var received = await connection.WaitAsync(KeepAliveInterval, _cancellation.Token);
                        if (!received)
                        {
                            // A bounded probe detects dead idle sessions even when the network does not
                            // promptly close the socket. The queue's ordinary polling remains the fallback.
                            await using var probe = connection.CreateCommand("select 1");
                            probe.CommandTimeout = 5;
                            await probe.ExecuteScalarAsync(_cancellation.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    _firstListenAttempt.TrySetResult(false);
                    break;
                }
                catch (Exception e)
                {
                    _firstListenAttempt.TrySetResult(false);
                    failures++;
                    if (failures == 1 || failures % 10 == 0)
                    {
                        _logger.LogWarning(e,
                            "PostgreSQL queue notifications are disconnected for database {Database}; queue polling remains active",
                            _databaseName);
                    }
                }
                finally
                {
                    if (connection != null)
                    {
                        connection.Notification -= onNotification;
                        try
                        {
                            await connection.DisposeAsync();
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug(e,
                                "Could not dispose a PostgreSQL queue notification connection for database {Database}",
                                _databaseName);
                        }
                    }
                }

                if (_cancellation.IsCancellationRequested) break;

                var retryDelay = TimeSpan.FromMilliseconds(Math.Min(failures * 500, 5000));
                try
                {
                    await Task.Delay(retryDelay, _cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            if (_task != null)
            {
#pragma warning disable VSTHRD003
                await _task;
#pragma warning restore VSTHRD003
            }

            _cancellation.Dispose();
        }
    }
}

internal static class PostgresqlQueueNotification
{
    internal const string Channel = "wolverine_queue_wakeup";

    public static string Key(PostgresqlQueue queue)
    {
        // Table identifiers are already sanitized and shortened for PostgreSQL. Including the
        // transport schema avoids waking a queue with the same name in another schema.
        return $"{queue.Parent.TransportSchemaName}.{queue.QueueTable.Identifier.Name}";
    }

    internal static string ApplicationName(string transportSchemaName)
    {
        return $"wolverine_qnotify_{transportSchemaName}";
    }

    public static async Task PublishAsync(NpgsqlConnection connection, string queueKey, ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.CreateCommand("select pg_notify(:channel, :payload)")
                .With("channel", Channel)
                .With("payload", queueKey)
                .ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // A notification only improves latency. Never turn a committed queue write into a
            // reported send failure; the listener's polling loop still finds the message.
            logger?.LogWarning(e,
                "Could not publish a PostgreSQL queue wakeup for {Queue}; polling remains active", queueKey);
        }
    }
}
