using System.Text;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Protocol;

namespace MQTTnet.Extensions.ManagedClient;

public class ManagedMqttClientOptions
{
    public MqttClientOptions ClientOptions { get; set; } = new();
}

public class ManagedMqttClientOptionsBuilder
{
    private MqttClientOptions _clientOptions = new();

    public ManagedMqttClientOptionsBuilder WithMaxPendingMessages(int _)
    {
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
        return new ManagedMqttClientOptions { ClientOptions = _clientOptions };
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
    private ManagedMqttClientOptions _options = new();

    public ManagedMqttClient(IMqttClient client)
    {
        _client = client;
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
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(_options.ClientOptions, CancellationToken.None);
        }
    }

    public async Task StopAsync()
    {
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None);
        }
    }

    public Task PingAsync(CancellationToken cancellationToken = default)
    {
        return _client.PingAsync(cancellationToken);
    }

    public Task EnqueueAsync(ManagedMqttApplicationMessage message)
    {
        return publishAsync(message.ApplicationMessage);
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

        return publishAsync(message);
    }

    public Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        return _client.SubscribeAsync(topic, qualityOfServiceLevel, CancellationToken.None);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private async Task publishAsync(MqttApplicationMessage message)
    {
        if (!_client.IsConnected)
        {
            await _client.ConnectAsync(_options.ClientOptions, CancellationToken.None);
        }

        await _client.PublishAsync(message, CancellationToken.None);
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
