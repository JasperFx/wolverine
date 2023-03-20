using Wolverine.Configuration;
using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

public class PostgresQueueSubscriberConfiguration : SubscriberConfiguration<
    PostgresQueueSubscriberConfiguration,
    PostgresQueue>
{
    public PostgresQueueSubscriberConfiguration(PostgresQueue endpoint) : base(endpoint)
    {
    }

    // TODO Cleanup
    // /// <summary>
    // ///     Configure the underlying Azure Service Bus queue. This is only applicable when
    // ///     Wolverine is creating the queues
    // /// </summary>
    // /// <param name="configure"></param>
    // /// <returns></returns>
    // public PostgresQueueSubscriberConfiguration ConfigureQueue(Action<CreateQueueOptions> configure)
    // {
    //     add(e => configure(e.Options));
    //     return this;
    // }
}