using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class ignoring_framework_and_telemetry_messages : IDisposable
{
    private readonly IHost _host;
    private readonly TrackedSession theSession;

    public ignoring_framework_and_telemetry_messages()
    {
        _host = WolverineHost.Basic();
        theSession = new TrackedSession(_host);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    private void record(object message)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Message = message;
        theSession.Record(MessageEventType.Sent, envelope, "service", Guid.NewGuid());
    }

    [Fact]
    public void telemetry_messages_marked_with_INotToBeRouted_are_not_tracked()
    {
        // Continuously published framework telemetry (e.g. the CritterWatch
        // monitoring messages) would otherwise hold a tracked session open
        // until it times out
        record(new FakeTelemetryMessage());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void agent_commands_are_still_ignored()
    {
        record(new FakeAgentCommand());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void acknowledgements_are_still_tracked()
    {
        // The tracked session has first-class acknowledgement semantics
        // (SendMessageAndWaitForAcknowledgementAsync), so acks must keep
        // being recorded even though Acknowledgement is INotToBeRouted
        record(new Acknowledgement());

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void failure_acknowledgements_are_still_tracked()
    {
        // AssertAnyFailureAcknowledgements reads FailureAcknowledgement records
        record(new FailureAcknowledgement { RequestId = Guid.NewGuid(), Message = "boom" });

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void ordinary_application_messages_are_still_tracked()
    {
        record(new FakeApplicationMessage());

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    private class FakeTelemetryMessage : INotToBeRouted;

    private class FakeApplicationMessage;

    private class FakeAgentCommand : IAgentCommand
    {
        public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentCommands.Empty);
        }
    }
}
