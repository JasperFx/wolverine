using ChaosTesting.Scripts;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.RabbitMQ;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ChaosTesting;

public class ChaosSpecifications
{
    private readonly ITestOutputHelper _output;
    private static TransportConfiguration RabbitMqOneInlineListener = new TransportConfiguration("Rabbit MQ Inline w/ One Listener")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting(),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting()
    };

    private static TransportConfiguration RabbitMqFiveParallelInlineListeners = new TransportConfiguration("Rabbit MQ Inline w/ Five Listeners")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.ListenerCount(5)),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting()
    };

    private static TransportConfiguration RabbitMqBufferedListener = new TransportConfiguration("Rabbit MQ Buffered w/ 1 Listener")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.BufferedInMemory()),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureSenders(x => x.BufferedInMemory())
    };

    private static TransportConfiguration RabbitMqDurableListener = new TransportConfiguration("Rabbit MQ Durable w/ 1 Listener")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.UseDurableInbox()),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureSenders(x => x.UseDurableOutbox())
    };

    private static TransportConfiguration RabbitMqFiveBufferedListeners = new TransportConfiguration("Rabbit MQ Buffered w/ 5 Listeners")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.BufferedInMemory().ListenerCount(5)),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureSenders(x => x.BufferedInMemory())
    };

    private static TransportConfiguration RabbitMqFiveDurableListeners = new TransportConfiguration("Rabbit MQ Durable w/ 5 Listeners")
    {
        ConfigureReceiver = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureListeners(x => x.UseDurableInbox().ListenerCount(5)),
        ConfigureSender = opts => opts.UseRabbitMq().AutoProvision().UseConventionalRouting().ConfigureSenders(x => x.UseDurableOutbox())
    };


    public ChaosSpecifications(ITestOutputHelper output)
    {
        _output = output;
    }

    protected async Task execute<TStorage, TScript>(TransportConfiguration configuration)
        where TStorage : IMessageStorageStrategy, new()
        where TScript : ChaosScript, new()
    {
        using var driver = new ChaosDriver(_output, new TStorage(), configuration);
        await driver.InitializeAsync();

        var script = new TScript();
        await script.Drive(driver);

        var completed = await driver.WaitForAllMessagingToComplete(script.TimeOut);

        completed.ShouldBeTrue();

        // COOLDOWN!!!!
        await Task.Delay(5.Seconds());
    }

    [Fact]
    public Task RabbitMqOneInlineListener_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqFiveBufferedListeners);


    [Fact]
    public Task RabbitMqDurableListener_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_Simple() =>
        execute<MartenStorageStrategy, Simplistic>(RabbitMqFiveDurableListeners);

    [Fact]
    public Task RabbitMqOneInlineListener_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqFiveBufferedListeners);


    [Fact]
    public Task RabbitMqDurableListener_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_ReceiverStartsLater() =>
        execute<MartenStorageStrategy, ReceiverStartsLater>(RabbitMqFiveDurableListeners);

    [Fact]
    public Task RabbitMqOneInlineListener_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqFiveBufferedListeners);


    [Fact]
    public Task RabbitMqDurableListener_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<MartenStorageStrategy, ReceiverGoesUpAndDown>(RabbitMqFiveDurableListeners);
}