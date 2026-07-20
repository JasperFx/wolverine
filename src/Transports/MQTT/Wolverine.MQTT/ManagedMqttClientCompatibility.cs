using System.Text;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Protocol;

namespace MQTTnet.Extensions.ManagedClient;

public class ManagedMqttClientOptions
{
    public MqttClientOptions ClientOptions { get; set; } = new();
    public int MaxPendingMessages { get; set; } = 10000;
    public TimeSpan AutoReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);
}

public class ManagedMqttClientOptionsBuilder
{
    private MqttClientOptions _clientOptions = new();
    private int _maxPendingMessages = 10000;
    private TimeSpan _autoReconnectDelay = TimeSpan.FromSeconds(5);

    public ManagedMqttClientOptionsBuilder WithMaxPendingMessages(int maxPendingMessages)
    {
        if (maxPendingMessages <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingMessages), "Max pending messages must be greater than zero");
        }

        _maxPendingMessages = maxPendingMessages;
        return this;
    }

    public ManagedMqttClientOptionsBuilder WithAutoReconnectDelay(TimeSpan autoReconnectDelay)
    {
        if (autoReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(autoReconnectDelay), "Auto reconnect delay must be greater than zero");
        }

        _autoReconnectDelay = autoReconnectDelay;
        return this;
    }

    public ManagedMqttClientOptionsBuilder WithClientOptions(Action<MqttClientOptionsBuilder> configure)
    {
        var builder = new MqttClientOptionsBuilder();
        configure(builder);
        _clientOptions = builder.Build();

        return this;
    }

    public ManagedMqttClientOptions Build()
    {
        return new ManagedMqttClientOptions
        {
            ClientOptions = _clientOptions,
            MaxPendingMessages = _maxPendingMessages,
            AutoReconnectDelay = _autoReconnectDelay
        };
    }
}

public class ManagedMqttApplicationMessage
{
    public MqttApplicationMessage ApplicationMessage { get; set; } = null!;
    public Guid Id { get; set; }
}

public interface IManagedMqttClient : IDisposable
{
    event Func<MqttApplicationMessageReceivedEventArgs, Task> ApplicationMessageReceivedAsync;
    event Func<MqttClientConnectedEventArgs, Task> ConnectedAsync;
    event Func<MqttClientDisconnectedEventArgs, Task> DisconnectedAsync;

    bool IsConnected { get; }
    IMqttClient InternalClient { get; }

    Task StartAsync(ManagedMqttClientOptions options);
    Task StopAsync();
    Task PingAsync(CancellationToken cancellationToken = default);
    Task EnqueueAsync(ManagedMqttApplicationMessage message);
    Task EnqueueAsync(string topic, string payload, MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce, bool retain = false);
    Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce);
}

internal class ManagedMqttClient : IManagedMqttClient
{
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly Queue<MqttApplicationMessage> _pendingMessages = new();
    private readonly SemaphoreSlim _publishingLock = new(1, 1);
    private readonly object _reconnectLock = new();
    private readonly Dictionary<string, MqttQualityOfServiceLevel> _subscriptions = new();
    private CancellationTokenSource? _lifetime;
    private ManagedMqttClientOptions _options = new();
    private Task? _reconnectTask;
    private bool _started;
    private bool _stopping;

    public ManagedMqttClient(IMqttClient client)
    {
        _client = client;
        _client.DisconnectedAsync += onClientDisconnectedAsync;
    }

    public event Func<MqttApplicationMessageReceivedEventArgs, Task> ApplicationMessageReceivedAsync
    {
        add => _client.ApplicationMessageReceivedAsync += value;
        remove => _client.ApplicationMessageReceivedAsync -= value;
    }

    public event Func<MqttClientConnectedEventArgs, Task> ConnectedAsync
    {
        add => _client.ConnectedAsync += value;
        remove => _client.ConnectedAsync -= value;
    }

    public event Func<MqttClientDisconnectedEventArgs, Task> DisconnectedAsync
    {
        add => _client.DisconnectedAsync += value;
        remove => _client.DisconnectedAsync -= value;
    }

    public bool IsConnected => _client.IsConnected;
    public IMqttClient InternalClient => _client;

    public async Task StartAsync(ManagedMqttClientOptions options)
    {
        _options = options;
        _stopping = false;
        _started = true;
        _lifetime = new CancellationTokenSource();

        try
        {
            await ensureConnectedAsync(_lifetime.Token);
            await tryPublishQueuedMessagesAsync(_lifetime.Token);
        }
        catch
        {
            scheduleReconnect();
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _started = false;

        var lifetime = _lifetime;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }

        lifetime?.Dispose();
        _lifetime = null;
    }

    public Task PingAsync(CancellationToken cancellationToken = default)
    {
        return _client.PingAsync(cancellationToken);
    }

    public Task EnqueueAsync(ManagedMqttApplicationMessage message)
    {
        enqueue(message.ApplicationMessage);
        return publishQueuedMessagesAsync();
    }

    public Task EnqueueAsync(string topic, string payload, MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce, bool retain = false)
    {
        var message = new MqttApplicationMessage
        {
            Topic = topic,
            PayloadSegment = Encoding.UTF8.GetBytes(payload),
            QualityOfServiceLevel = qualityOfServiceLevel,
            Retain = retain
        };

        enqueue(message);
        return publishQueuedMessagesAsync();
    }

    public async Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        lock (_subscriptions)
        {
            _subscriptions[topic] = qualityOfServiceLevel;
        }

        if (!_client.IsConnected)
        {
            scheduleReconnect();
            return;
        }

        try
        {
            await _client.SubscribeAsync(topic, qualityOfServiceLevel, CancellationToken.None);
        }
        catch
        {
            scheduleReconnect();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _started = false;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _client.DisconnectedAsync -= onClientDisconnectedAsync;
        _client.Dispose();
        _connectionLock.Dispose();
        _publishingLock.Dispose();
    }

    private void enqueue(MqttApplicationMessage message)
    {
        lock (_pendingLock)
        {
            if (_pendingMessages.Count >= _options.MaxPendingMessages)
            {
                throw new InvalidOperationException($"The managed MQTT client has reached the configured pending message limit of {_options.MaxPendingMessages}");
            }

            _pendingMessages.Enqueue(message);
        }
    }

    private async Task ensureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_client.IsConnected)
            {
                await _client.ConnectAsync(_options.ClientOptions, cancellationToken);
            }

            await restoreSubscriptionsAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task publishQueuedMessagesAsync()
    {
        await tryPublishQueuedMessagesAsync(CancellationToken.None);
    }

    private async Task<bool> tryPublishQueuedMessagesAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return true;
        }

        if (!_client.IsConnected)
        {
            scheduleReconnect();
            return false;
        }

        await _publishingLock.WaitAsync(cancellationToken);
        try
        {
            while (_client.IsConnected)
            {
                MqttApplicationMessage? message;
                lock (_pendingLock)
                {
                    if (_pendingMessages.Count == 0)
                    {
                        return true;
                    }

                    message = _pendingMessages.Peek();
                }

                try
                {
                    await _client.PublishAsync(message, cancellationToken);

                    lock (_pendingLock)
                    {
                        if (_pendingMessages.Count > 0 && ReferenceEquals(_pendingMessages.Peek(), message))
                        {
                            _pendingMessages.Dequeue();
                        }
                    }
                }
                catch
                {
                    scheduleReconnect();
                    return false;
                }
            }
        }
        finally
        {
            _publishingLock.Release();
        }

        scheduleReconnect();
        return false;
    }

    private async Task restoreSubscriptionsAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, MqttQualityOfServiceLevel>[] subscriptions;
        lock (_subscriptions)
        {
            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            await _client.SubscribeAsync(subscription.Key, subscription.Value, cancellationToken);
        }
    }

    private Task onClientDisconnectedAsync(MqttClientDisconnectedEventArgs _)
    {
        if (_started && !_stopping)
        {
            scheduleReconnect();
        }

        return Task.CompletedTask;
    }

    private void scheduleReconnect()
    {
        if (!_started || _stopping)
        {
            return;
        }

        lock (_reconnectLock)
        {
            if (_reconnectTask is null || _reconnectTask.IsCompleted)
            {
                _reconnectTask = Task.Run(reconnectAsync);
            }
        }
    }

    private async Task reconnectAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null)
        {
            return;
        }

        while (_started && !_stopping && !lifetime.IsCancellationRequested)
        {
            try
            {
                await ensureConnectedAsync(lifetime.Token);
                var published = await tryPublishQueuedMessagesAsync(lifetime.Token);

                if (_client.IsConnected && published)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // MQTTnet's managed client used to hide transient broker failures. Keep trying here as long as
                // the Wolverine transport is still running.
            }

            try
            {
                await Task.Delay(_options.AutoReconnectDelay, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }
}

public static class MqttClientFactoryCompatibilityExtensions
{
    public static IManagedMqttClient CreateManagedMqttClient(this MqttClientFactory factory)
    {
        return new ManagedMqttClient(factory.CreateMqttClient());
    }

    public static IManagedMqttClient CreateManagedMqttClient(this MqttClientFactory factory, IMqttNetLogger logger)
    {
        return new ManagedMqttClient(factory.CreateMqttClient(logger));
    }
}
