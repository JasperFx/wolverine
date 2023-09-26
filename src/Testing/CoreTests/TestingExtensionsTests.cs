using TestMessages;
using Xunit;

namespace CoreTests;

public class TestingExtensionsTests
{
    [Fact]
    public void should_be_no_messages_happy_path()
    {
        new object[0].ShouldHaveNoMessages();
    }

    [Fact]
    public void should_be_no_messages_sad_path()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { "hello", "goodbye" }.ShouldHaveNoMessages();
        });

        ex.Message.ShouldBe("Should be no messages, but was hello, goodbye");
    }

    [Fact]
    public void should_be_no_message_of_type_T_happy_path()
    {
        new object[0].ShouldHaveNoMessageOfType<Message2>();
        new object[] { new Message1(), new Envelope { Message = new Message3() } }
            .ShouldHaveNoMessageOfType<Message2>();
    }

    [Fact]
    public void should_be_no_message_of_type_T_sad_path()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { new Message1(), new Envelope { Message = new Message3() } }
                .ShouldHaveNoMessageOfType<Message1>();
        });

        ex.Message.ShouldBe(
            "Should be no messages of type TestMessages.Message1, but the actual messages were TestMessages.Message1, TestMessages.Message3");
    }

    [Fact]
    public void should_be_no_message_of_type_T_sad_path_through_delivery_message()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { new DeliveryMessage<Message1>(new Message1(), new DeliveryOptions()) }
                .ShouldHaveNoMessageOfType<Message1>();
        });

        ex.Message.ShouldBe(
            "Should be no messages of type TestMessages.Message1, but the actual messages were Wolverine.DeliveryMessage`1[TestMessages.Message1]");
    }

    [Fact]
    public void should_be_no_message_of_type_T_sad_path_through_envelope()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { new Message1(), new Envelope { Message = new Message3() } }
                .ShouldHaveNoMessageOfType<Message3>();
        });

        ex.Message.ShouldBe(
            "Should be no messages of type TestMessages.Message3, but the actual messages were TestMessages.Message1, TestMessages.Message3");
    }

    [Fact]
    public void should_be_message_of_type_happy_path()
    {
        var message1 = new Message1();
        new object[] { message1, new Message2(), new Message3() }
            .ShouldHaveMessageOfType<Message1>()
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public void should_be_message_of_type_happy_path_with_null_delivery_options()
    {
        var message1 = new Message1();
        new object[] { message1, new Message2(), new Message3() }
            .ShouldHaveMessageOfType<Message1>(options => options.ShouldBeNull())
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public void should_be_message_of_type_happy_path_through_delivery_message()
    {
        var message1 = new Message1();
        var delivery1 = new DeliveryMessage<Message1>(message1, new DeliveryOptions());
        new object[] { delivery1, new Message2(), new Message3() }
            .ShouldHaveMessageOfType<Message1>()
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public void should_be_message_of_type_happy_path_with_delivery_options()
    {
        var message1 = new Message1();
        var delivery1 = new DeliveryMessage<Message1>(message1, new DeliveryOptions { TenantId = "xyz" });
        new object[] { delivery1, new Message2(), new Message3() }
            .ShouldHaveMessageOfType<Message1>(options => options.ShouldNotBeNull().TenantId.ShouldBe("xyz"))
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public void should_be_message_of_type_happy_path_through_envelope()
    {
        var message1 = new Message1();
        new object[] { new Envelope { Message = message1 }, new Message2(), new Message3() }
            .ShouldHaveMessageOfType<Message1>()
            .ShouldBeSameAs(message1);
    }

    [Fact]
    public void should_be_message_of_type_sad_path_wrong_types()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { new Message1(), new Message2(), new Message3() }
                .ShouldHaveMessageOfType<Message4>();
        });

        ex.Message.ShouldBe(
            "Should be a message of type TestMessages.Message4, but actual messages were TestMessages.Message1, TestMessages.Message2, TestMessages.Message3");
    }

    [Fact]
    public void should_be_message_of_type_sad_path_no_messages()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[0]
                .ShouldHaveMessageOfType<Message4>();
        });

        ex.Message.ShouldBe("Should be a message of type TestMessages.Message4, but there were no messages");
    }

    [Fact]
    public void find_envelope_happy_path()
    {
        var message1 = new Message1();
        var env = new object[] { new Envelope { Message = message1 }, new Message2(), new Message3() }
            .ShouldHaveEnvelopeForMessageType<Message1>();

        env.Message.ShouldBeSameAs(message1);
    }

    [Fact]
    public void find_envelope_sad_path()
    {
        var ex = Should.Throw<WolverineMessageExpectationException>(() =>
        {
            new object[] { new Envelope { Message = new Message2() }, new Message2(), new Message3() }
                .ShouldHaveEnvelopeForMessageType<Message1>();
        });
    }
}