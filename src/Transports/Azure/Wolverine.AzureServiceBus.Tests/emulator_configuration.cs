using Shouldly;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class emulator_configuration
{
    [Fact]
    public void use_the_default_emulator_connection_strings()
    {
        var options = new WolverineOptions();
        options.UseAzureServiceBusEmulator();

        var transport = options.AzureServiceBusTransport();

        transport.ConnectionString.ShouldBe(AzureServiceBusEmulatorExtensions.DefaultEmulatorConnectionString);
        transport.ManagementConnectionString
            .ShouldBe(AzureServiceBusEmulatorExtensions.DefaultEmulatorManagementConnectionString);
    }

    [Fact]
    public void use_explicit_emulator_connection_strings()
    {
        var options = new WolverineOptions();
        options.UseAzureServiceBusEmulator("Endpoint=sb://localhost:5673;UseDevelopmentEmulator=true;",
            "Endpoint=sb://localhost:5301;UseDevelopmentEmulator=true;");

        var transport = options.AzureServiceBusTransport();

        transport.ConnectionString.ShouldBe("Endpoint=sb://localhost:5673;UseDevelopmentEmulator=true;");
        transport.ManagementConnectionString.ShouldBe("Endpoint=sb://localhost:5301;UseDevelopmentEmulator=true;");
    }

    [Fact]
    public void the_destructive_cleanup_is_not_enabled_by_default()
    {
        var options = new WolverineOptions();
        options.UseAzureServiceBusEmulator();

        options.AzureServiceBusTransport().DeleteAllExistingObjectsOnStartup.ShouldBeFalse();
    }

    [Fact]
    public void opt_into_the_destructive_cleanup()
    {
        var options = new WolverineOptions();
        options.UseAzureServiceBusEmulator().DeleteAllExistingObjectsOnStartup();

        options.AzureServiceBusTransport().DeleteAllExistingObjectsOnStartup.ShouldBeTrue();
    }
}
