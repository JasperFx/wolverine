namespace Wolverine.RabbitMQ;

// Legacy interface for exchange -> queue binding expressions
public interface IRabbitMqBindableExchange : IRabbitMqExchange
{
    /// <summary>
    /// Binds this exchange to a provided topic
    /// </summary>
    /// <param name="topicPattern"></param>
    /// <returns></returns>
    public TopicBindingExchange BindTopic(string topicPattern);

    /// <summary>
    /// Binds this exchange to the provided queue
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="bindingKey"></param>
    /// <returns></returns>
    public RabbitMqBinding BindQueue(string queueName, string? bindingKey = null);

    /// <summary>
    /// Bind a source exchange to this exchange (this exchange is the destination).
    /// Messages published to the source exchange will be routed to this exchange.
    /// </summary>
    /// <param name="sourceExchangeName">The exchange that receives published messages</param>
    /// <param name="bindingKey">Optional routing/binding key</param>
    /// <returns></returns>
    public RabbitMqExchangeBinding BindExchange(string sourceExchangeName, string? bindingKey = null);

}