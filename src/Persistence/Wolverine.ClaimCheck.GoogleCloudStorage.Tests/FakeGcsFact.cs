using System.Runtime.CompilerServices;
using IntegrationTests;

namespace Wolverine.ClaimCheck.GoogleCloudStorage.Tests;

/// <summary>
/// Where the fake-gcs-server emulator lives, and whether it is up. Tests skip cleanly when it is not.
/// </summary>
internal static class FakeGcs
{
    public const string Host = "localhost";
    public const int Port = 4443;
    public const string EmulatorHost = "http://localhost:4443";

    public static bool IsRunning => EmulatorProbe.IsListening(Host, Port);

    public const string SkipReason =
        "The fake-gcs-server emulator is not running on localhost:4443. " +
        "Start it with `docker compose up -d fake-gcs-server` from the repo root to enable these tests.";
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when the fake-gcs-server emulator is not reachable.
/// </summary>
public sealed class FakeGcsFactAttribute : FactAttribute
{
    public FakeGcsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!FakeGcs.IsRunning)
        {
            Skip = FakeGcs.SkipReason;
        }
    }
}
