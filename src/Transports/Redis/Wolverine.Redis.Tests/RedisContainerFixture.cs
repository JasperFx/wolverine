using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Testcontainers.Redis;

namespace Wolverine.Redis.Tests;

public static class RedisContainerFixture
{
    private static RedisContainer? _container;

    public static string ConnectionString { get; private set; } = "localhost:6379";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Testcontainers logs a "Connected to Docker" banner through a default console logger.
        // Under xUnit v3 the test project is an executable that speaks JSON to the runner over
        // stdout, and a [ModuleInitializer] runs BEFORE Main -- so before xUnit has redirected
        // Console.Out. The banner lands in the raw protocol channel and the whole assembly dies
        // with "Test process did not return valid JSON", running no tests at all.
        _container = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithLogger(NullLogger.Instance)
            .Build();

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _container.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        ConnectionString = _container.GetConnectionString();
    }
}
