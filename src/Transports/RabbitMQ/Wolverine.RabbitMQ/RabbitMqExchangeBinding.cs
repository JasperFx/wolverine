using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ;

public class RabbitMqExchangeBinding
{
    public RabbitMqExchangeBinding(string sourceExchangeName, string destinationExchangeName, string? bindingKey = null)
    {
        SourceExchangeName = sourceExchangeName ?? throw new ArgumentNullException(nameof(sourceExchangeName));
        DestinationExchangeName = destinationExchangeName ?? throw new ArgumentNullException(nameof(destinationExchangeName));
        BindingKey = bindingKey ?? $"{sourceExchangeName}_{destinationExchangeName}";
    }

    public string BindingKey { get; }
    public string SourceExchangeName { get; }
    public string DestinationExchangeName { get; }

    public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
    public bool HasDeclared { get; private set; }

    internal async Task DeclareAsync(IChannel channel, ILogger logger)
    {
        await channel.ExchangeBindAsync(DestinationExchangeName, SourceExchangeName, BindingKey, Arguments);
        logger.LogInformation(
            "Declared a Rabbit Mq exchange binding '{Key}' from exchange {Source} to exchange {Destination}",
            BindingKey, SourceExchangeName, DestinationExchangeName);

        HasDeclared = true;
    }

    public async Task TeardownAsync(IChannel channel)
    {
        await channel.ExchangeUnbindAsync(DestinationExchangeName, SourceExchangeName, BindingKey, Arguments);
    }

    protected bool Equals(RabbitMqExchangeBinding other)
    {
        return BindingKey == other.BindingKey
            && SourceExchangeName == other.SourceExchangeName
            && DestinationExchangeName == other.DestinationExchangeName;
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((RabbitMqExchangeBinding)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(BindingKey, SourceExchangeName, DestinationExchangeName);
    }

    public override string ToString()
    {
        return
            $"{nameof(BindingKey)}: {BindingKey}, {nameof(SourceExchangeName)}: {SourceExchangeName}, {nameof(DestinationExchangeName)}: {DestinationExchangeName}";
    }
}
