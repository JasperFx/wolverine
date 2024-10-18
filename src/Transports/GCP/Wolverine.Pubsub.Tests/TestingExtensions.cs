using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions {
    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options) => options.UsePubsub(Environment.GetEnvironmentVariable("PUBSUB_PROJECT_ID") ?? throw new NullReferenceException(), opts => {
        opts.EmulatorDetection = EmulatorDetection.EmulatorOnly;
    });
}
