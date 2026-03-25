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
        _container = new ContainerBuilder()
            .WithImage("eclipse-mosquitto:2")
            .WithPortBinding(1883, true)
            .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("mosquitto version"))
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();
        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(1883);
    }
}
