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
        // An already-running Redis wins. The container start below sits on the critical path of
        // *every* process launch -- it runs before Main -- so a caller that supplies a warm broker
        // (a dev, or a CI lane sharing one) skips it entirely. Same shape as Servers.cs.
        var configured = Environment.GetEnvironmentVariable("WOLVERINE_REDIS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured.Trim();
            return;
        }

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

        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown();
    }

    /// <summary>
    /// Stop the container on a graceful exit. Testcontainers' Ryuk reaper normally handles this, but
    /// it is not always running (ryuk.disabled=true is a common local setting) and the leak is *per
    /// process*: per-class isolation and the flaky-retry harness both spawn extra processes, so a
    /// single run can strand several containers that then live forever. A kill -9 still needs Ryuk;
    /// this only covers the common case.
    /// </summary>
    private static void shutdown()
    {
        var container = Interlocked.Exchange(ref _container, null);
        if (container == null) return;

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        container.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
    }
}
