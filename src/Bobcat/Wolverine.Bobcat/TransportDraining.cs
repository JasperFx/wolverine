using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Bobcat;

/// <summary>
/// Clears what a Wolverine application left on its <b>broker</b> between scenarios — the half of a
/// per-scenario reset that a store reset does not reach (bobcat#282).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> A scenario acts, its handler cascades, and those messages go to a real
/// broker. The scenario ends, the resource's reset clears the document store and the envelope
/// tables — and the messages already sitting on the broker survive it. They are delivered inside
/// the <em>next</em> scenario, against a store that no longer contains what they refer to, and the
/// failure names an id belonging to a scenario that already passed. So the natural reading is
/// "the scenario that failed is broken" when the cause is the one before it.
/// </para>
/// <para>
/// <b>Why it stayed invisible.</b> An in-memory transport has nowhere for a message to survive a
/// reset. Every sample either stubs the transport or does not cascade across one inside a
/// scenario, so nothing in the suite could see it — which is why the test that pins this takes a
/// real RabbitMQ rather than a stub.
/// </para>
/// <para>
/// <b>What it does not solve.</b> A scenario that ends with its own cascade <em>still in flight</em>
/// — enqueued in Wolverine's outbound buffer, not yet on the broker — can have that message
/// delivered after the purge, because there was nothing on the broker to purge when the reset ran.
/// That is a different problem (waiting for quiescence before the reset, bobcat#282's option 2)
/// and this deliberately does not pretend to cover it. Acts that go through the tracked session —
/// every shipped <c>When</c> step — already wait for what they caused, which is what keeps that
/// case rare.
/// </para>
/// <para>
/// <b>Not automatic.</b> This is a reset a suite opts into, for the same reason the store reset
/// is: purging is destructive, and a suite whose broker is shared with something else — a
/// developer watching a queue, a second application in a modular monolith — must not have Bobcat
/// empty it without being asked. Wire it where the store reset already goes:
/// </para>
/// <code>
/// new HostResource&lt;Program&gt;(reset: async host =&gt;
/// {
///     await host.ResetEventStoresAsync();    // whatever the suite already did
///     await host.DrainTransportsAsync();     // …and the broker too
/// });
/// </code>
/// </remarks>
public static class TransportDraining
{
    /// <summary>
    /// Purge every broker queue this application declares, and report how many were purged.
    /// </summary>
    /// <remarks>
    /// Only <see cref="IBrokerQueue"/> endpoints are touched: those are the ones with state living
    /// outside the process, which is the entire problem. Local queues are in-memory and die with
    /// the scope; an endpoint whose transport is stubbed has nothing to purge and is skipped
    /// rather than failed, so the same reset line is safe in a suite that stubs its transports.
    /// </remarks>
    public static async Task<int> DrainTransportsAsync(this IHost host, CancellationToken token = default)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // Stop listening BEFORE purging, and restart after. Without the bracket the purge races
        // the listeners it is trying to make pointless: a consumer can pull the very message being
        // purged into its in-process buffer a moment before the purge lands, and an in-process
        // message is past the reach of anything the broker can be told to do.
        var listening = runtime.Endpoints.ActiveListeners()
            .Select(agent => runtime.Endpoints.EndpointFor(agent.Uri))
            .Where(endpoint => endpoint != null)
            .ToList();

        foreach (var endpoint in listening)
        {
            await runtime.Endpoints.StopListenerAsync(endpoint!, token).ConfigureAwait(false);
        }

        var purged = 0;
        try
        {
        foreach (var transport in runtime.Options.Transports)
        {
            foreach (var endpoint in transport.Endpoints())
            {
                if (endpoint is not IBrokerQueue queue) continue;

                token.ThrowIfCancellationRequested();

                // A queue that was never provisioned — a publish-only endpoint on a broker the
                // suite never listened to — throws rather than reporting empty. That is not a
                // reset failure: there is demonstrably nothing on it to leak.
                try
                {
                    await queue.PurgeAsync(runtime.Logger).ConfigureAwait(false);
                    purged++;
                }
                catch (Exception e)
                {
                    runtime.Logger.LogDebug(e,
                        "Bobcat could not purge {Endpoint} between scenarios; it may not be provisioned.",
                        endpoint.Uri);
                }
            }
        }
        }
        finally
        {
            // In a finally because a half-purged suite that has also stopped listening is worse
            // than an unpurged one: every scenario after it would hang waiting for a message
            // nothing is there to receive.
            foreach (var endpoint in listening)
            {
                await runtime.Endpoints.StartListenerAsync(endpoint!, token).ConfigureAwait(false);
            }
        }

        return purged;
    }
}
