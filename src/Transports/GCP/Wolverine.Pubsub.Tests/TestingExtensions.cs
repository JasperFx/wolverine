using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions
{
    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options)
    {
        // Use localhost (not the IPv6 literal [::1]) so this resolves to the IPv4 address that
        // Docker publishes the emulator on. CI Linux runners publish the container port on IPv4
        // only, where [::1]:8085 is unreachable. See #3191.
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "localhost:8085");
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        return options
            .UsePubsub("wolverine")
            .UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
    }
}