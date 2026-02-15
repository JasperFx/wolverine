using Wolverine.Http;

namespace WolverineWebApi;

public record CreateContentItemV1(string Name);

public record CreateContentItemV2(string Name, string Category);

public record ContentItemCreated(string Name, string? Category, string Version);

public static class ContentNegotiationEndpoints
{
    [WolverinePost("/content-negotiation/items"), AcceptsContentType("application/vnd.item.v1+json")]
    public static ContentItemCreated CreateV1(CreateContentItemV1 command)
    {
        return new ContentItemCreated(command.Name, null, "v1");
    }

    [WolverinePost("/content-negotiation/items"), AcceptsContentType("application/vnd.item.v2+json")]
    public static ContentItemCreated CreateV2(CreateContentItemV2 command)
    {
        return new ContentItemCreated(command.Name, command.Category, "v2");
    }
}
