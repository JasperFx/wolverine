using System;
using System.Collections.Generic;

namespace Wolverine.RabbitMQ.Internal;

public interface IBindingExpression
{
    /// <summary>
    ///     Bind the named exchange to a queue. The routing key will be
    ///     [exchange name]_[queue name]
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
    /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
    IRabbitMqTransportExpression ToQueue(string queueName, Action<RabbitMqQueue>? configure = null,
        Dictionary<string, object>? arguments = null);

    /// <summary>
    ///     Bind the named exchange to a queue with a user supplied binding key
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="bindingKey"></param>
    /// <param name="configure">Optional configuration of the Rabbit MQ queue</param>
    /// <param name="arguments">Optional configuration for arguments to the Rabbit MQ binding</param>
    IRabbitMqTransportExpression ToQueue(string queueName, string bindingKey,
        Action<RabbitMqQueue>? configure = null, Dictionary<string, object>? arguments = null);
}