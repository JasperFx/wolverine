using System;
using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

public interface IPublishToExpression
{
    /// <summary>
    ///     All matching records are to be sent to the configured subscriber
    ///     by Uri
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="address"></param>
    /// <returns></returns>
    ISubscriberConfiguration To(Uri uri);

    /// <summary>
    ///     Send all the matching messages to the designated Uri string
    /// </summary>
    /// <param name="uriString"></param>
    /// <returns></returns>
    ISubscriberConfiguration To(string uriString);

    /// <summary>
    ///     Publish the designated message types to the named
    ///     local queue
    /// </summary>
    /// <param name="queueName"></param>
    /// <returns></returns>
    LocalQueueConfiguration ToLocalQueue(string queueName);

    /// <summary>
    ///     Publishes the matching messages locally to the default
    ///     local queue
    /// </summary>
    LocalQueueConfiguration Locally();
}