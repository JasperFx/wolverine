using DotNet.Testcontainers.Builders;
using Testcontainers.ServiceBus;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class Playing
{
    private readonly ServiceBusContainer _serviceBusContainer;
    public const ushort ServiceBusPort = 5672;
    public const ushort ServiceBusHttpPort = 5300;
    
    [Fact]
    public async Task spin_up_container()
    {
        
        var container = new ServiceBusBuilder()
            .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
            .WithAcceptLicenseAgreement(true)
            .WithPortBinding(ServiceBusPort, true)
            .WithPortBinding(ServiceBusHttpPort, true)
            .WithEnvironment("SQL_WAIT_INTERVAL", "0")
            .WithResourceMapping("Config.json", "/ServiceBus_Emulator/ConfigFiles/")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request =>
                request.ForPort(ServiceBusHttpPort).ForPath("/health")))
            .Build();
    }
}