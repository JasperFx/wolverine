using CoreTests.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     <see cref="BrokerResource.Setup" /> used to log a per-endpoint failure and return, so
///     <c>resources setup</c> reported success having created nothing. A deploy step that provisions broker
///     objects ahead of its hosts then exited 0 while the hosts failed on the missing queue - the failure
///     surfaced a long way from its cause, in a different process.
/// </summary>
public class BrokerResourceSetupTests
{
    private readonly MockWolverineRuntime theRuntime = new();

    [Fact]
    public async Task throws_when_an_endpoint_cannot_be_set_up()
    {
        var resource = new BrokerResource(transportWith(new StubBrokerEndpoint { WillFail = true }), theRuntime);

        var aggregate = await Should.ThrowAsync<AggregateException>(() => resource.Setup(CancellationToken.None));

        aggregate.InnerExceptions.Count.ShouldBe(1);
    }

    /// <summary>
    ///     Collect-then-throw, matching <see cref="BrokerResource.Check" />: one broker object that cannot be
    ///     provisioned must not stop the others from being attempted, or a single failure hides the state of
    ///     everything after it.
    /// </summary>
    [Fact]
    public async Task attempts_every_endpoint_before_throwing()
    {
        var failing = new StubBrokerEndpoint { WillFail = true };
        var succeeding = new StubBrokerEndpoint();

        var resource = new BrokerResource(transportWith(failing, succeeding), theRuntime);

        await Should.ThrowAsync<AggregateException>(() => resource.Setup(CancellationToken.None));

        succeeding.WasSetUp.ShouldBeTrue();
    }

    [Fact]
    public async Task does_not_throw_when_every_endpoint_can_be_set_up()
    {
        var endpoints = new[] { new StubBrokerEndpoint(), new StubBrokerEndpoint() };

        await new BrokerResource(transportWith(endpoints), theRuntime).Setup(CancellationToken.None);

        endpoints.ShouldAllBe(x => x.WasSetUp);
    }

    private static IBrokerTransport transportWith(params StubBrokerEndpoint[] endpoints)
    {
        var transport = Substitute.For<IBrokerTransport>();
        transport.Name.Returns("Fake");
        transport.ResourceUri.Returns(new Uri("fake://"));
        transport.Endpoints().Returns(endpoints);

        return transport;
    }

    private class StubBrokerEndpoint()
        : Endpoint(new Uri("fake://queue"), EndpointRole.Application), IBrokerEndpoint
    {
        public bool WillFail { get; init; }

        public bool WasSetUp { get; private set; }

        public ValueTask SetupAsync(ILogger logger)
        {
            if (WillFail)
            {
                throw new DivideByZeroException("the calling role has no rights to create this");
            }

            WasSetUp = true;

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> CheckAsync() => throw new NotSupportedException();

        public ValueTask TeardownAsync(ILogger logger) => throw new NotSupportedException();

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) =>
            throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }
}
