using Wolverine.Transports.Postgresql.Internal;

namespace Wolverine.Transports.Postgresql;

public sealed class PostgresConfiguration : BrokerExpression<PostgresTransport, PostgresQueue,
    PostgresQueue, PostgresQueueListenerConfiguration, PostgresQueueSubscriberConfiguration,
    PostgresConfiguration>
{
    public PostgresConfiguration(PostgresTransport transport, WolverineOptions options) : base(transport,
        options)
    {
    }

    protected override PostgresQueueListenerConfiguration createListenerExpression(
        PostgresQueue listenerEndpoint)
    {
        return new PostgresQueueListenerConfiguration(listenerEndpoint);
    }

    protected override PostgresQueueSubscriberConfiguration createSubscriberExpression(
        PostgresQueue subscriberEndpoint)
    {
        return new PostgresQueueSubscriberConfiguration(subscriberEndpoint);
    }

    // /// <summary>
    // ///     Add explicit configuration to an Postgres queue that is being created by
    // ///     this application
    // /// </summary>
    // /// <param name="queueName"></param>
    // /// <param name="configure"></param>
    // /// <returns></returns>
    // public PostgresConfiguration ConfigureQueue(string queueName, Action<CreateQueueOptions> configure)
    // {
    //     if (configure == null)
    //     {
    //         throw new ArgumentNullException(nameof(configure));
    //     }
    //
    //     var queue = Transport.Queues[queueName];
    //     configure(queue.Options);
    //
    //     return this;
    // }
    //
    public PostgresConfiguration UseConventionalRouting(
        Action<PostgresMessageRoutingConvention>? configure = null)
    {
        var routing = new PostgresMessageRoutingConvention();
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }
}