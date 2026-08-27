using System.Runtime.CompilerServices;
using IntegrationTests;

namespace Wolverine.ClaimCheck.Nats.Tests;

/// <summary>
/// Where NATS lives, and whether it is up. Tests skip cleanly when it is not.
/// </summary>
internal static class NatsServer
{
    public const string Host = "localhost";
    public const int Port = 4222;
    public const string Url = "nats://localhost:4222";

    public static bool IsRunning => EmulatorProbe.IsListening(Host, Port);

    public const string SkipReason =
        "NATS is not running on localhost:4222. " +
        "Start it with `docker compose up -d nats` from the repo root to enable these tests.";
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when NATS is not reachable on its default port.
/// </summary>
public sealed class NatsFactAttribute : FactAttribute
{
    public NatsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!NatsServer.IsRunning)
        {
            Skip = NatsServer.SkipReason;
        }
    }
}
