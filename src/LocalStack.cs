using System.Runtime.CompilerServices;
using Amazon.S3;

namespace IntegrationTests;

/// <summary>
/// Where LocalStack lives, and whether it is up. Tests skip cleanly when it is not.
/// </summary>
internal static class LocalStack
{
    public const string Host = "localhost";
    public const int Port = 4566;
    public const string ServiceUrl = "http://localhost:4566";

    public static bool IsRunning => EmulatorProbe.IsListening(Host, Port);

    public const string SkipReason =
        "LocalStack is not running on localhost:4566. " +
        "Start it with `docker compose up -d localstack` from the repo root to enable these tests.";

    public static AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            UseHttp = true
        };

        return new AmazonS3Client("xxx", "xxx", config);
    }
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when LocalStack is not
/// reachable on its default port.
/// </summary>
public sealed class LocalStackFactAttribute : FactAttribute
{
    public LocalStackFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!LocalStack.IsRunning)
        {
            Skip = LocalStack.SkipReason;
        }
    }
}
