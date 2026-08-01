using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Wolverine.MQTT.Tests;

public static class MosquittoContainerFixture
{
    private static IContainer? _container;

    public static string Host { get; private set; } = "localhost";
    public static int Port { get; private set; } = 1883;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // An already-running broker wins. The container start below sits on the critical path of
        // *every* process launch -- it runs before Main -- so a caller that supplies a warm broker
        // (a dev, or a CI lane sharing one) skips it entirely. Same shape as Servers.cs.
        var configured = Environment.GetEnvironmentVariable("WOLVERINE_MQTT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var parts = configured.Trim().Split(':', 2);
            Host = parts[0];
            Port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 1883;
            return;
        }

        // Testcontainers logs a "Connected to Docker" banner through a default console logger.
        // Under xUnit v3 the test project is an executable that speaks JSON to the runner over
        // stdout, and a [ModuleInitializer] runs BEFORE Main -- so before xUnit has redirected
        // Console.Out. The banner lands in the raw protocol channel and the whole assembly dies
        // with "Test process did not return valid JSON", running no tests at all.
        _container = new ContainerBuilder()
            .WithImage("eclipse-mosquitto:2")
            .WithPortBinding(1883, true)
            .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version"))
            .WithLogger(NullLogger.Instance)
            .Build();

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _container.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(1883);

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
