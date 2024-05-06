using System;
using Wolverine.Attributes;
using Wolverine.Runtime.Routing;
using Xunit;

namespace CoreTests.Configuration;

#region sample_using_Topic_attribute

[Topic("one")]
public class TopicMessage1;

#endregion

public class ColorMessagee
{
    public string Color { get; set; }
}

[MessageIdentity("one")]
public class M1;

[Topic("two")]
public class M2;

[Topic("three")]
[MessageIdentity("third")]
public class M3;

public class TopicRoutingTester
{
    [Theory]
    [InlineData(typeof(M1), "one")]
    [InlineData(typeof(M2), "two")]
    [InlineData(typeof(M3), "three")]
    public void determine_topic_name_by_type(Type messageType, string expected)
    {
        // Do it repeatedly just to hammer on the memoization a bit
        TopicRouting.DetermineTopicName(messageType).ShouldBe(expected);
        TopicRouting.DetermineTopicName(messageType).ShouldBe(expected);
        TopicRouting.DetermineTopicName(messageType).ShouldBe(expected);
    }

    [Fact]
    public void use_explicit_topic_on_envelope()
    {
        TopicRouting.DetermineTopicName(new Envelope(new M1()) { TopicName = "foo" }).ShouldBe("foo");
    }

    [Fact]
    public void use_message_type_topic_if_no_explicit_topic()
    {
        TopicRouting.DetermineTopicName(new Envelope(new M1())).ShouldBe(TopicRouting.DetermineTopicName(typeof(M1)));
    }
}