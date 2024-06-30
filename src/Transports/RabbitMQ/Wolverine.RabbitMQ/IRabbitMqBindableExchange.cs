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
    
}