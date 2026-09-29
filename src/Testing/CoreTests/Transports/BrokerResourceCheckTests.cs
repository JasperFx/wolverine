using CoreTests.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     GH-4693. <see cref="BrokerResource.Check" /> folded "the check threw" into the same bucket as "the
///     resource is absent", so the summary an operator reads asserted the one explanation the code did not
///     know to be true.
///
///     <para>Seen for real: <c>SqlServerQueue.CheckAsync()</c> hit a <c>NullReferenceException</c> out of
///     Weasel's delta comparison (JasperFx/weasel#658) and <c>resources check</c> reported
///     <c>Missing known broker resources: sqlserver://sr11/, ...</c> — with the actual cause readable only
///     as a separate Error log line above the summary. <c>resources check</c> is a deploy gate, so a
///     confident wrong diagnosis is worse than a vague right one.</para>
/// </summary>
public class BrokerResourceCheckTests
{
    private readonly MockWolverineRuntime theRuntime = new();

    private static IBrokerTransport transportWith(params StubCheckEndpoint[] endpoints)
    {
        var transport = Substitute.For<IBrokerTransport>();
        transport.Name.Returns("Fake");
        transport.ResourceUri.Returns(new Uri("fake://"));
        transport.Endpoints().Returns(endpoints);

        return transport;
    }

    [Fact]
    public async Task passes_when_every_endpoint_reports_that_it_exists()
    {
        var resource = new BrokerResource(
            transportWith(new StubCheckEndpoint("fake://one"), new StubCheckEndpoint("fake://two")), theRuntime);

        await resource.Check(CancellationToken.None);
    }

    [Fact]
    public async Task an_endpoint_that_reports_absent_is_still_called_missing()
    {
        // The pre-existing behaviour, unchanged: a check that ran and answered "no" is a genuine absence
        // and must keep saying so.
        var resource = new BrokerResource(
            transportWith(new StubCheckEndpoint("fake://gone") { Exists = false }), theRuntime);

        var ex = await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));

        ex.Message.ShouldContain("Missing known broker resources");
        ex.Message.ShouldContain("fake://gone");
    }

    [Fact]
    public async Task an_endpoint_whose_check_throws_is_not_reported_as_missing()
    {
        // The bug. This endpoint may well exist -- nobody established otherwise -- so claiming it is
        // missing is a guess presented as a finding.
        var resource = new BrokerResource(
            transportWith(new StubCheckEndpoint("fake://unknowable")
            {
                Failure = new NullReferenceException("Object reference not set to an instance of an object.")
            }), theRuntime);

        var ex = await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));

        ex.Message.ShouldNotContain("Missing known broker resources");
        ex.Message.ShouldContain("Unable to check broker resources");

        // The cause travels in the summary, not only in a log line the reader has to scroll back for.
        ex.Message.ShouldContain("fake://unknowable");
        ex.Message.ShouldContain(nameof(NullReferenceException));
        ex.Message.ShouldContain("Object reference not set");
    }

    [Fact]
    public async Task a_thrown_check_still_fails_the_gate()
    {
        // Failing is the safe default: not knowing whether a broker object exists is not a reason to let
        // a deploy through. The change is what it SAYS, not whether it stops.
        var resource = new BrokerResource(
            transportWith(new StubCheckEndpoint("fake://unknowable") { Failure = new DivideByZeroException() }),
            theRuntime);

        await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));
    }

    [Fact]
    public async Task the_two_kinds_of_failure_are_reported_together_and_separately()
    {
        var resource = new BrokerResource(transportWith(
            new StubCheckEndpoint("fake://gone") { Exists = false },
            new StubCheckEndpoint("fake://unknowable") { Failure = new TimeoutException("broker did not answer") },
            new StubCheckEndpoint("fake://fine")), theRuntime);

        var ex = await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));

        ex.Message.ShouldContain("Missing known broker resources");
        ex.Message.ShouldContain("fake://gone");
        ex.Message.ShouldContain("Unable to check broker resources");
        ex.Message.ShouldContain("fake://unknowable");

        // A healthy endpoint appears in neither list.
        ex.Message.ShouldNotContain("fake://fine");

        // And the absent one is not described as unverifiable, nor the unverifiable one as absent --
        // which a single combined list could not express at all.
        ex.Message.Split(Environment.NewLine)
            .Single(x => x.StartsWith("Missing")).ShouldNotContain("fake://unknowable");
    }

    [Fact]
    public async Task a_thrown_check_keeps_its_stack_trace_as_the_inner_exception()
    {
        // The summary carries the type and message; anything catching this still needs the stack.
        var failure = new NullReferenceException("boom");
        var resource = new BrokerResource(
            transportWith(new StubCheckEndpoint("fake://unknowable") { Failure = failure }), theRuntime);

        var ex = await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));

        ex.InnerException.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task every_endpoint_is_checked_even_after_one_throws()
    {
        // Collect-then-throw, matching Setup: one broken check must not hide the state of everything
        // after it.
        var failing = new StubCheckEndpoint("fake://unknowable") { Failure = new DivideByZeroException() };
        var later = new StubCheckEndpoint("fake://later");

        var resource = new BrokerResource(transportWith(failing, later), theRuntime);

        await Should.ThrowAsync<Exception>(() => resource.Check(CancellationToken.None));

        later.WasChecked.ShouldBeTrue();
    }

    private class StubCheckEndpoint(string uri)
        : Endpoint(new Uri(uri), EndpointRole.Application), IBrokerEndpoint
    {
        public bool Exists { get; init; } = true;
        public Exception? Failure { get; init; }
        public bool WasChecked { get; private set; }

        public ValueTask<bool> CheckAsync()
        {
            WasChecked = true;

            if (Failure != null)
            {
                throw Failure;
            }

            return ValueTask.FromResult(Exists);
        }

        public ValueTask SetupAsync(ILogger logger) => throw new NotSupportedException();

        public ValueTask TeardownAsync(ILogger logger) => throw new NotSupportedException();

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) =>
            throw new NotSupportedException();

        protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();
    }
}
