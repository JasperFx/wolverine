using Wolverine.Attributes;

namespace CoreTests.Acceptance;

public class sticky_message_handlers : IntegrationContext
{
    public sticky_message_handlers(DefaultApp @default) : base(@default)
    {
    }


}

public class StickyMessage{}

[StickyHandler("blue")]
public static class BlueStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message)
    {
        return new StickyMessageResponse("blue", message);
    }
}

[StickyHandler("green")]
public static class GreenStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message)
    {
        return new StickyMessageResponse("green", message);
    }
}

public class StickyMessageResponse(string Color, StickyMessage Message);

public class StickyMessageResponseHandler
{
    public static void Handle(StickyMessageResponse response)
    {
        // nothing
    }
}