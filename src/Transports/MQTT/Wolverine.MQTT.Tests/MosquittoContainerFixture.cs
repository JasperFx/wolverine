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
    }
}
