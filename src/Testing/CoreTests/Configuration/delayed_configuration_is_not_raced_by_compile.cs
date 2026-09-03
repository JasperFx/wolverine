using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4262: <see cref="Endpoint.Compile"/> snapshotted <c>DelayedConfiguration</c> with
/// <c>ToArray()</c> while that same <see cref="List{T}"/> was mutated from two other places holding
/// DIFFERENT locks — <c>RegisterDelayedConfiguration</c> (from the
/// <see cref="DelayedEndpointConfiguration{TEndpoint}"/> constructor, which a routing convention calls
/// under its own <c>_senderRegistrationLock</c>) and the removal at the end of that class's
/// <c>Apply()</c>. <c>Compile</c> itself runs under <c>EndpointCollection._channelLock</c>, so neither
/// mutation was excluded.
///
/// <para>A <see cref="List{T}"/> racing its own <c>ToArray()</c> yields an array containing a NULL
/// element in BOTH directions — <c>RemoveAt</c> nulls the vacated slot after decrementing <c>_size</c>,
/// and <c>Add</c> publishes the incremented <c>_size</c> before writing the element — and
/// <c>Compile</c> dereferenced it:</para>
///
/// <code>
/// System.NullReferenceException: Object reference not set to an instance of an object.
///    at Wolverine.Configuration.Endpoint.Compile(IWolverineRuntime runtime) Endpoint.cs:line 685
///    at Wolverine.Configuration.EndpointCollection.buildSendingAgent(...)
///    at Wolverine.Transports.MessageRoutingConvention`4...DiscoverSenders(...)
///    at Wolverine.Runtime.WolverineRuntime.findRoutes(Type messageType)
/// </code>
///
/// <para>It was captured in the field on the first-publish route-building path, where it killed the
/// Marten projection shard that was raising the side effect: the shard was left Paused indefinitely
/// with this Wolverine-internal stack as its pause reason, while four sibling shards on the same
/// runtime kept running. Reported from CritterWatch.</para>
///
/// <para>⚠️ These are stress tests. They cannot prove a race absent, and before the fix they failed
/// within tens of rounds rather than reliably on round one — so treat a single green run as weak and
/// the round counts as deliberately generous.</para>
/// </summary>
public class delayed_configuration_is_not_raced_by_compile
{
    private const int Rounds = 750;
    private const int RegisterRounds = 20_000;

    private static IWolverineRuntime runtimeForCompile()
    {
        var options = new WolverineOptions();
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        return runtime;
    }

    [Fact]
    public async Task compile_does_not_race_a_configuration_applying_itself()
    {
        var runtime = runtimeForCompile();
        var token = TestContext.Current.CancellationToken;

        for (var round = 0; round < Rounds; round++)
        {
            var endpoint = new RacedEndpoint(new Uri($"stub://gh4262-apply-{round}"));
            var configurations = Enumerable
                .Range(0, 12)
                .Select(_ => (IDelayedEndpointConfiguration)new RacedConfiguration(endpoint))
                .ToArray();

            await Should.NotThrowAsync(async () =>
            {
                using var gate = new ManualResetEventSlim();

                // Applying removes each configuration from the endpoint's list...
                var applying = Task.Run(() =>
                {
                    gate.Wait(token);
                    foreach (var configuration in configurations) configuration.Apply();
                }, token);

                // ...while Compile snapshots that same list.
                var compiling = Task.Run(() =>
                {
                    gate.Wait(token);
                    endpoint.Compile(runtime);
                }, token);

                gate.Set();
                await Task.WhenAll(applying, compiling);
            });
        }
    }

    [Fact]
    public async Task compile_does_not_race_a_configuration_being_registered()
    {
        var runtime = runtimeForCompile();
        var token = TestContext.Current.CancellationToken;

        for (var round = 0; round < RegisterRounds; round++)
        {
            var endpoint = new RacedEndpoint(new Uri($"stub://gh4262-register-{round}"));

            await Should.NotThrowAsync(async () =>
            {
                using var gate = new ManualResetEventSlim();

                // The constructor registers, so this is the Add side of the same list.
                var registering = Task.Run(() =>
                {
                    gate.Wait(token);
                    for (var i = 0; i < 12; i++) _ = new RacedConfiguration(endpoint);
                }, token);

                var compiling = Task.Run(() =>
                {
                    gate.Wait(token);
                    endpoint.Compile(runtime);
                }, token);

                gate.Set();
                await Task.WhenAll(registering, compiling);
            });
        }
    }

    public class RacedEndpoint : Endpoint
    {
        public RacedEndpoint(Uri uri) : base(uri, EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            throw new NotSupportedException();
        }
    }

    public class RacedConfiguration : DelayedEndpointConfiguration<RacedEndpoint>
    {
        public RacedConfiguration(RacedEndpoint endpoint) : base(endpoint)
        {
            add(e => e.TelemetryEnabled = true);
        }
    }
}
