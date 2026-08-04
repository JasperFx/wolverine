using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Testcontainers.Pulsar;

namespace Wolverine.Pulsar.Tests;

public static class PulsarContainerFixture
{
    /// <summary>
    /// GH-3799. Pinned, and shared by every Pulsar container in this project.
    ///
    /// <para>4.2.4 is exactly what <c>apachepulsar/pulsar:latest</c> resolved to when this was
    /// pinned — and <c>:latest</c> had moved to it that same day, so CI's Pulsar was changing under
    /// the suite with nothing in the repository recording it. docker-compose.yml is set to the same
    /// version; it previously said 4.0.3, which meant a developer and CI were running different
    /// brokers for the same tests.</para>
    /// </summary>
    public const string PulsarImage = "apachepulsar/pulsar:4.2.4";

    private static PulsarContainer? _container;

    public static Uri ServiceUrl { get; private set; } = new("pulsar://localhost:6650");

    public static string HttpServiceUrl { get; private set; } = "http://localhost:8080";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // An already-running Pulsar wins. The container start below sits on the critical path of
        // *every* process launch -- it runs before Main -- and the Pulsar image is a heavy one, so a
        // caller that supplies a warm broker skips it entirely. Same shape as Servers.cs.
        var configured = Environment.GetEnvironmentVariable("WOLVERINE_PULSAR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ServiceUrl = new Uri(configured.Trim());
            HttpServiceUrl = Environment.GetEnvironmentVariable("WOLVERINE_PULSAR_HTTP")?.Trim()
                is { Length: > 0 } http
                ? http
                : $"http://{ServiceUrl.Host}:8080";
            return;
        }

        // Testcontainers logs a "Connected to Docker" banner through a default console logger.
        // Under xUnit v3 the test project is an executable that speaks JSON to the runner over
        // stdout, and a [ModuleInitializer] runs BEFORE Main -- so before xUnit has redirected
        // Console.Out. The banner lands in the raw protocol channel and the whole assembly dies
        // with "Test process did not return valid JSON", running no tests at all.
        _container = new PulsarBuilder()
            .WithImage(PulsarImage)
            .WithLogger(NullLogger.Instance)
            .Build();

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _container.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        ServiceUrl = new Uri(_container.GetBrokerAddress());

        var host = _container.Hostname;
        var httpPort = _container.GetMappedPublicPort(8080);
        HttpServiceUrl = $"http://{host}:{httpPort}";

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
