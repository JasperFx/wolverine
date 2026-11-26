using System.Net.Sockets;
using Google.Api.Gax;

namespace Wolverine.Pubsub.Tests;

public static class TestingExtensions
{
    /// <summary>
    /// The compose emulator, unless the process was started with WOLVERINE_PUBSUB_EMULATOR: LocalGate runs the suite on
    /// two lanes with an emulator each and tells every worker its lane's (GH-5958). Not PUBSUB_EMULATOR_HOST itself,
    /// which tests here point elsewhere on purpose before this is first read.
    /// </summary>
    public static readonly string EmulatorHost =
        Environment.GetEnvironmentVariable("WOLVERINE_PUBSUB_EMULATOR") is { Length: > 0 } configured
            ? configured.Trim()
            : "localhost:8085";

    public static PubsubConfiguration UsePubsubTesting(this WolverineOptions options)
    {
        // Use localhost (not the IPv6 literal [::1]) so this resolves to the IPv4 address that
        // Docker publishes the emulator on. CI Linux runners publish the container port on IPv4
        // only, where [::1]:8085 is unreachable. See #3191.
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", EmulatorHost);
        Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        return options
            .UsePubsub("wolverine")
            .UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
    }

    /// <summary>
    /// Register an additional, named Pub/Sub broker pointed at a second project on the same emulator. The emulator
    /// accepts arbitrary project ids at request time, so no compose change is needed — just a distinct project id.
    /// </summary>
    public static PubsubConfiguration AddNamedPubsubBrokerTesting(this WolverineOptions options, BrokerName name,
        string projectId)
    {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", EmulatorHost);

        return options
            .AddNamedPubsubBroker(name, projectId)
            .UseEmulatorDetection(EmulatorDetection.EmulatorOnly);
    }

    /// <summary>
    /// True when the Pub/Sub emulator TCP port is reachable. Integration tests skip-guard on this so the suite is
    /// green when Docker/the emulator is not running.
    /// </summary>
    public static async Task<bool> IsEmulatorAvailable()
    {
        try
        {
            using var client = new TcpClient();
            var separator = EmulatorHost.LastIndexOf(':');
            var connect = client.ConnectAsync(EmulatorHost[..separator], int.Parse(EmulatorHost[(separator + 1)..]));
            var completed = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(2)));
            return completed == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
