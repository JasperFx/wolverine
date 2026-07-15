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
    public void system_commands_are_not_tracked_by_default()
    {
        // Continuously published system/monitoring telemetry (e.g. the CritterWatch monitoring
        // messages) would otherwise hold a tracked session open until it times out.
        record(new FakeSystemCommand());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void system_commands_are_tracked_once_included()
    {
        theSession.IncludeSystemCommands();

        record(new FakeSystemCommand());

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void not_to_be_routed_is_not_by_itself_a_reason_to_ignore()
    {
        // INotToBeRouted governs conventional routing, NOT tracking. A real message can be
        // explicitly routed while carrying it (CritterWatch's monitoring commands do), and such a
        // message must remain trackable. Only ISystemCommand suppresses tracking.
        record(new FakeNotRoutedMessage());

        theSession.AllRecordsInOrder().Length.ShouldBe(1);
    }

    [Fact]
    public void agent_commands_are_still_ignored()
    {
        record(new FakeAgentCommand());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void agent_commands_are_ignored_even_when_system_commands_are_included()
    {
        theSession.IncludeSystemCommands();

        record(new FakeAgentCommand());

        theSession.AllRecordsInOrder().ShouldBeEmpty();
    }

    [Fact]
    public void acknowledgements_are_still_tracked()
    {
        // The tracked session has first-class acknowledgement semantics
        // (SendMessageAndWaitForAcknowledgementAsync), so acks must keep being recorded.
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

    private class FakeSystemCommand : ISystemCommand;

    private class FakeNotRoutedMessage : INotToBeRouted;

    private class FakeApplicationMessage;

    private class FakeAgentCommand : IAgentCommand
    {
        public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        {
            return Task.FromResult(AgentCommands.Empty);
        }
    }
}
