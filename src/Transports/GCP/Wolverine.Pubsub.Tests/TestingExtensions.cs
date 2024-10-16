using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions {
    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options) => options.UsePubsub("wolverine", opts => {
        opts.EmulatorDetection = EmulatorDetection.EmulatorOnly;
    });
}
