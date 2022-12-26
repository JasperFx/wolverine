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

    /// <summary>
    ///     Bind a named queue to this exchange with an optional binding key
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="bindingKey"></param>
    /// <returns></returns>
    RabbitMqBinding BindQueue(string queueName, string? bindingKey = null);

    /// <summary>
    ///     Declare a Rabbit MQ binding with the supplied topic pattern to
    ///     the queue
    /// </summary>
    /// <param name="topicPattern"></param>
    /// <param name="bindingName"></param>
    /// <exception cref="NotImplementedException"></exception>
    TopicBinding BindTopic(string topicPattern);
}