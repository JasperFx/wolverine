using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions
{
    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options)
    {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        return options
            .UsePubsub("wolverine")
            .UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
    }
}