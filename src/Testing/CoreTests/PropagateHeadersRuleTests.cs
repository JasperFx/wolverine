using NSubstitute;
using Wolverine.ComplianceTests;
using Xunit;

namespace CoreTests;

public class PropagateHeadersRuleTests
{
    [Fact]
    public void modify_is_a_no_op_outside_of_a_handler_context()
    {
        var rule = new PropagateHeadersRule(["x-custom"]);
        var envelope = ObjectMother.Envelope();

        rule.Modify(envelope);

        envelope.Headers.ContainsKey("x-custom").ShouldBeFalse();
    }

    [Fact]
    public void copies_named_headers_from_incoming_to_outgoing()
    {
        var rule = new PropagateHeadersRule(["x-custom", "x-other"]);

        var incoming = ObjectMother.Envelope();
        incoming.Headers["x-custom"] = "custom-value";
        incoming.Headers["x-other"] = "other-value";

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns(incoming);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers["x-custom"].ShouldBe("custom-value");
        outgoing.Headers["x-other"].ShouldBe("other-value");
    }

    [Fact]
    public void headers_not_present_on_incoming_are_silently_skipped()
    {
        var rule = new PropagateHeadersRule(["x-present", "x-missing"]);

        var incoming = ObjectMother.Envelope();
        incoming.Headers["x-present"] = "value";

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns(incoming);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers["x-present"].ShouldBe("value");
        outgoing.Headers.ContainsKey("x-missing").ShouldBeFalse();
    }

    [Fact]
    public void no_op_when_there_is_no_incoming_envelope()
    {
        var rule = new PropagateHeadersRule(["x-custom"]);

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns((Envelope?)null);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers.ContainsKey("x-custom").ShouldBeFalse();
    }
}

public class PropagateOneHeaderRuleTests
{
    [Fact]
    public void modify_is_a_no_op_outside_of_a_handler_context()
    {
        var rule = new PropagateOneHeaderRule("x-on-behalf-of");
        var envelope = ObjectMother.Envelope();

        rule.Modify(envelope);

        envelope.Headers.ContainsKey("x-on-behalf-of").ShouldBeFalse();
    }

    [Fact]
    public void copies_single_header_from_incoming_to_outgoing()
    {
        var rule = new PropagateOneHeaderRule("x-on-behalf-of");

        var incoming = ObjectMother.Envelope();
        incoming.Headers["x-on-behalf-of"] = "admin-user";
        incoming.Headers["x-other"] = "should-not-propagate";

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns(incoming);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers["x-on-behalf-of"].ShouldBe("admin-user");
        outgoing.Headers.ContainsKey("x-other").ShouldBeFalse();
    }

    [Fact]
    public void header_not_present_on_incoming_is_silently_skipped()
    {
        var rule = new PropagateOneHeaderRule("x-on-behalf-of");

        var incoming = ObjectMother.Envelope();

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns(incoming);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers.ContainsKey("x-on-behalf-of").ShouldBeFalse();
    }

    [Fact]
    public void no_op_when_there_is_no_incoming_envelope()
    {
        var rule = new PropagateOneHeaderRule("x-on-behalf-of");

        var context = Substitute.For<IMessageContext>();
        context.Envelope.Returns((Envelope?)null);

        var outgoing = ObjectMother.Envelope();
        rule.ApplyCorrelation(context, outgoing);

        outgoing.Headers.ContainsKey("x-on-behalf-of").ShouldBeFalse();
    }
}
