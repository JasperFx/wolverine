using JasperFx.Core.Reflection;
using Wolverine.RabbitMQ;

namespace ChaosTesting;

public class ChaosSpecifications
{
    static ChaosSpecifications()
    {
        AllScripts = typeof(ChaosSpecifications)
            .Assembly
            .GetTypes()
            .Where(x => x.IsConcreteTypeOf<ChaosScript>())
            .Select(Activator.CreateInstance)
            .OfType<ChaosScript>()
            .ToArray();
    }

    public static ChaosScript[] AllScripts { get; }

    public static IMessageStorageStrategy[] Storage { get; } = { new MartenStorageStrategy() };

    public static IEnumerable<TransportConfiguration> TransportConfigurations()
    {
        // yield return new TransportConfiguration("Rabbit MQ Inline w/ One Listener")
        // {
        //     ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting(),
        //     ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting()
        // };
        
        yield return new TransportConfiguration("Rabbit MQ Buffered w/ One Listener")
        {
            ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.BufferedInMemory()),
            ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureSenders(x => x.BufferedInMemory())
        };
    }

    public static IEnumerable<object[]> Data()
    {
        foreach (var transportConfiguration in TransportConfigurations())
        {
            foreach (var storageStrategy in Storage)
            {
                foreach (var script in AllScripts)
                {
                    yield return new object[] {transportConfiguration, storageStrategy, script};
                }
            }
        }
    }
    
    [Theory]
    [MemberData(nameof(Data))]
    public async Task chaos_and_load_specs(TransportConfiguration transportConfiguration, IMessageStorageStrategy storageStrategy, ChaosScript script)
    {
        using var driver = new ChaosDriver(storageStrategy, transportConfiguration);
        await driver.InitializeAsync();

        await script.Drive(driver);

        var completed = await driver.WaitForAllMessagingToComplete(script.TimeOut);
    }
}