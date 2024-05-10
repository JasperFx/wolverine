using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MQTTnet.Diagnostics;

namespace Wolverine.MQTT.Internals;

public class MqttNetLogger : IMqttNetLogger
{
    private readonly ILogger _logger;

    public MqttNetLogger(ILogger<MqttClient> logger)
    {
        _logger = logger;
    }

    public void Publish(MqttNetLogLevel logLevel, string source, string message, object[] parameters, Exception exception)
    {
        switch (logLevel)
        {
            case MqttNetLogLevel.Error:
                _logger.LogError(exception, message, parameters);
                break;

            case MqttNetLogLevel.Info:
                _logger.LogInformation(message, parameters);
                break;

            case MqttNetLogLevel.Warning:
                _logger.LogWarning(message, parameters);
                break;

            case MqttNetLogLevel.Verbose:
                _logger.LogDebug(message, parameters);
                break;
        }
    }

    public bool IsEnabled => _logger.IsEnabled(LogLevel.Debug);
}