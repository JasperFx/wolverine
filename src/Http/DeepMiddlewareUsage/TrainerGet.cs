using Wolverine.Http;

namespace DeepMiddlewareUsage;

public static class TrainerGet
{
    [Tags("Trainer")]
    [WolverineGet("/api/trainer")]
    public static TrainerResponse Get(Trainer trainer)
    {
        return new TrainerResponse(trainer.Name ?? "Unknown", trainer.Country);
    }
}

public record TrainerResponse(string Name, string? Country);