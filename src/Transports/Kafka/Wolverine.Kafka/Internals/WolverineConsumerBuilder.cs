using Confluent.Kafka;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// GH-4522. Confluent's <see cref="ConsumerBuilder{TKey,TValue}.SetErrorHandler"/> throws if a handler is
/// already registered, so once a user claimed it through <c>ConfigureConsumerBuilders(...)</c> Wolverine
/// could not install its own -- and silently gave up, leaving the transport's connection state pinned at
/// Unknown forever. Health checks, <c>wolverine-diagnostics</c> and CritterWatch then could not tell a
/// healthy Kafka consumer from one that had been disconnected for an hour.
///
/// <para>
/// The handler field itself is <c>protected internal</c> on the builder, so a subclass can read what the
/// user registered and replace it with a composition of both. Neither handler can see the other, and the
/// user's runs first so a throw from it cannot be blamed on Wolverine's tracking.
/// </para>
/// </summary>
internal class WolverineConsumerBuilder : ConsumerBuilder<string, byte[]>
{
    public WolverineConsumerBuilder(ConsumerConfig config) : base(config)
    {
    }

    /// <summary>
    /// True when a user handler was already registered and Wolverine's tracking was composed in behind it,
    /// rather than registered on its own. Purely informational -- the tracking works either way.
    /// </summary>
    public bool ComposedWithUserHandler { get; private set; }

    /// <summary>
    /// Install <paramref name="wolverineHandler"/> so it always runs, whether or not user configuration
    /// already registered an error handler of its own.
    /// </summary>
    public void ComposeErrorHandler(Action<IConsumer<string, byte[]>, Error> wolverineHandler)
    {
        var userHandler = ErrorHandler;

        if (userHandler == null)
        {
            ErrorHandler = wolverineHandler;
            return;
        }

        ComposedWithUserHandler = true;

        ErrorHandler = (consumer, error) =>
        {
            try
            {
                userHandler(consumer, error);
            }
            finally
            {
                // Wolverine's tracking must run even when the user's handler throws. It only ever moves
                // the connection state toward trouble, so it cannot mask anything the user's handler did,
                // and one bad callback must not silently reinstate the bug this fixes.
                wolverineHandler(consumer, error);
            }
        };
    }

    /// <summary>
    /// Fire whatever error handler is currently installed. Only exists so the composition can be tested
    /// without standing up a real consumer against a broker -- librdkafka is the only other caller.
    /// </summary>
    internal void InvokeErrorHandlerForTesting(Error error)
    {
        ErrorHandler?.Invoke(null!, error);
    }
}
