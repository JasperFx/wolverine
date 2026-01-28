namespace Wolverine.MQTT;

public record MqttJwtAuthenticationOptions(Func<Task<byte[]>> GetTokenCallBack, TimeSpan RefreshPeriod);