using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_516_allow_for_middleware_to_overwrite_the_original_message : IntegrationContext
{
    public Bug_516_allow_for_middleware_to_overwrite_the_original_message(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task able_to_use_the_replaced_message()
    {
        await Host.InvokeMessageAndWaitAsync(new ReplacedMessage("Original"));

        ReplacedMessageHandler.Handled.Name.ShouldBe("Original-Replaced-Again-Tuple");
    }
}

public record ReplacedMessage(string Name);

public static class ReplacedMessageHandler
{
    public static ReplacedMessage Before(ReplacedMessage message)
    {
        return new ReplacedMessage(message.Name + "-Replaced");
    }

    public static Task<ReplacedMessage> BeforeAsync(ReplacedMessage message)
    {
        return Task.FromResult(new ReplacedMessage(message.Name + "-Again"));
    }

    public static (ReplacedMessage, HandlerContinuation) Before(ReplacedMessage message, Envelope envelope)
    {
        return (new ReplacedMessage(message.Name + "-Tuple"), HandlerContinuation.Continue);
    }

    public static void Handle(ReplacedMessage message)
    {
        Handled = message;
    }

    public static ReplacedMessage Handled { get; set; }
}