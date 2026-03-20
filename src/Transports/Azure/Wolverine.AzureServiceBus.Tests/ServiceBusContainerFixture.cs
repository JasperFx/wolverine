using System.Runtime.CompilerServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Testcontainers.MsSql;
using Testcontainers.ServiceBus;

namespace Wolverine.AzureServiceBus.Tests;

public static class ServiceBusContainerFixture
{
    private static INetwork? _network;
    private static MsSqlContainer? _sqlContainer;
    private static ServiceBusContainer? _serviceBusContainer;

    public static string ConnectionString { get; private set; } =
        "Endpoint=sb://localhost:5673;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    [ModuleInitializer]
    internal static void Initialize()
    {
        _network = new NetworkBuilder()
            .Build();

        _network.CreateAsync().GetAwaiter().GetResult();

        const string sqlPassword = "Strong_password_123!";
        const string sqlNetworkAlias = "sqledge";

        _sqlContainer = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/azure-sql-edge:latest")
            .WithNetwork(_network)
            .WithNetworkAliases(sqlNetworkAlias)
            .WithPassword(sqlPassword)
            .Build();

        _sqlContainer.StartAsync().GetAwaiter().GetResult();

        _serviceBusContainer = new ServiceBusBuilder()
            .WithAcceptLicenseAgreement(true)
            .WithMsSqlContainer(_network, _sqlContainer, sqlNetworkAlias, sqlPassword)
            .Build();

        _serviceBusContainer.StartAsync().GetAwaiter().GetResult();
        ConnectionString = _serviceBusContainer.GetConnectionString();
    }
}
