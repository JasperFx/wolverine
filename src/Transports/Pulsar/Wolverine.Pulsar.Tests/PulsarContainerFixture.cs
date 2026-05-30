using System.Runtime.CompilerServices;
using Testcontainers.Pulsar;

namespace Wolverine.Pulsar.Tests;

public static class PulsarContainerFixture
{
    private static PulsarContainer? _container;

    public static Uri ServiceUrl { get; private set; } = new("pulsar://localhost:6650");

    public static string HttpServiceUrl { get; private set; } = "http://localhost:8080";

    [ModuleInitializer]
    internal static void Initialize()
    {
        _container = new PulsarBuilder()
            .WithImage("apachepulsar/pulsar:latest")
            .Build();

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _container.StartAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

        ServiceUrl = new Uri(_container.GetBrokerAddress());

        var host = _container.Hostname;
        var httpPort = _container.GetMappedPublicPort(8080);
        HttpServiceUrl = $"http://{host}:{httpPort}";
    }
}
