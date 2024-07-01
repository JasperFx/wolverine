using Wolverine.RabbitMQ.Internal;

namespace Wolverine.RabbitMQ;

public interface IRabbitMqExchange
{
    string Name { get; }

    /// <summary>
    ///     Should this exchange survive server restarts and last until they are explicitly deleted. The default is true
    /// </summary>
    bool IsDurable { get; set; }

    /// <summary>
    ///     Type of Rabbit MQ exchange. The default is fanout
    /// </summary>
    ExchangeType ExchangeType { get; set; }

    /// <summary>
    ///     If true, this exchange will be deleted when the connection is closed. Default is false
    /// </summary>
    bool AutoDelete { get; set; }

    IDictionary<string, object> Arguments { get; }
}