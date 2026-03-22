using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace CosmosDbTests.LeaderElection;

public static class CosmosDbContainerFixture
{
    private static IContainer? _container;

    public const string AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static string ConnectionString { get; private set; } =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [ModuleInitializer]
    internal static void Initialize()
    {
        _container = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview")
            .WithPortBinding(8081, true)
            .WithPortBinding(1234, true)
            .WithEnvironment("PROTOCOL", "https")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Gateway=OK"))
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(8081);
        ConnectionString = $"AccountEndpoint=https://{host}:{port}/;AccountKey={AccountKey}";
    }
}
