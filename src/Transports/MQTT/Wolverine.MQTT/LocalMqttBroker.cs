using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Debug;
using MQTTnet;
using MQTTnet.Diagnostics;
using MQTTnet.Server;

namespace Wolverine.MQTT;

public class LocalMqttBroker : IAsyncDisposable, IMqttNetLogger
{
    private MqttServer _mqttServer;
    private readonly MqttServerOptions _mqttServerOptions;

    public LocalMqttBroker()
    {
        // The port for the default endpoint is 1883.
        // The default endpoint is NOT encrypted!
        // Use the builder classes where possible.
        _mqttServerOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().Build();
        

    }

    public LocalMqttBroker(int port)
    {
        _mqttServerOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(port).Build();
    }

    public async Task StartAsync()
    {
        var mqttFactory = new MqttFactory(this);

        _mqttServer = mqttFactory.CreateMqttServer(_mqttServerOptions);

        await _mqttServer.StartAsync();

    }

    public async Task StopAsync()
    {
        if (_mqttServer == null) return;

        await _mqttServer.StopAsync();
    }

    public bool IsRunning => _mqttServer != null;

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            await StopAsync();
        }
        
        _mqttServer.Dispose();
    }

    public ILogger Logger { get; set; } = new DebugLoggerProvider().CreateLogger("MQTT");

    public void Publish(MqttNetLogLevel logLevel, string source, string message, object[] parameters, Exception exception)
    {
        switch (logLevel)
        {
            case MqttNetLogLevel.Error:
                Logger.LogError(exception, message, parameters);
                break;
            
            case MqttNetLogLevel.Info:
                Logger.LogInformation(message, parameters);
                break;
            
            case MqttNetLogLevel.Verbose:
                Logger.LogDebug(message, parameters);
                break;
            
            case MqttNetLogLevel.Warning:
                Logger.LogWarning(message, parameters);
                break;
        }
    }

    public bool IsEnabled => true;
}