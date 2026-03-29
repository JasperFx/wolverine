using Wolverine.Http;

namespace StartupStyleTarget;

public static class TestEndpoint
{
    [WolverineGet("/api/test")]
    public static string Get()
    {
        return "tested";
    }

    [WolverinePost("/api/items")]
    public static string Post(CreateItemRequest request)
    {
        return $"Created: {request.Name}";
    }
}

public record CreateItemRequest(string Name);
