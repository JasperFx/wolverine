using JasperFx.Core;
using Wolverine.Configuration;

namespace Wolverine.Transports;

/// <summary>
/// GH-5737. Implemented by an endpoint whose listener polls the broker for messages (Amazon SQS, Azure Service Bus
/// in buffered or durable mode, ...). After a receive comes back empty, the listener waits <see cref="IdleDelay" />
/// before it polls again, on top of any long-poll wait the receive itself already did. A quiet queue that is fine
/// with picking messages up late can raise this to cut the number of empty -- and on most cloud brokers, billable --
/// receive requests.
/// </summary>
/// <remarks>
/// Configure it with <see cref="ListenerConfiguration{TSelf,TEndpoint}.IdleDelay" />. Listeners read it through
/// <see cref="IdleDelays.For" />, which falls back to <see cref="IdleDelays.Default" />.
/// </remarks>
public interface ISupportIdleDelay
{
    /// <summary>
    /// How long the listener waits after an empty receive before it polls again. Null keeps the listener's default
    /// of <see cref="IdleDelays.Default" />.
    /// </summary>
    TimeSpan? IdleDelay { get; set; }
}

/// <summary>
/// GH-5737. The one place a polling listener resolves the delay it applies after an empty receive.
/// </summary>
public static class IdleDelays
{
    /// <summary>
    /// The delay a polling listener applies after an empty receive unless the endpoint says otherwise.
    /// </summary>
    public static readonly TimeSpan Default = 250.Milliseconds();

    /// <summary>
    /// The largest idle delay a listener can wait on. Wolverine waits on a semaphore so that a wakeup can cut the
    /// delay short, and that wait cannot be longer than <see cref="int.MaxValue" /> milliseconds.
    /// </summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// The idle delay configured on <paramref name="endpoint" />, or <see cref="Default" />.
    /// </summary>
    public static TimeSpan For(Endpoint endpoint)
    {
        return endpoint is ISupportIdleDelay { IdleDelay: { } delay } ? delay : Default;
    }

    internal static void AssertValid(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero || delay > Maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay,
                $"An idle delay must be between zero and {Maximum}.");
        }
    }
}
