using ChaosTesting.Scripts;
using JasperFx.Core;
using Shouldly;
using Wolverine.RabbitMQ;
using Wolverine.RDBMS.MultiTenancy;
using Xunit.Abstractions;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace ChaosTesting;

public class MultiTenantedChaosSpecifications
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


    public MultiTenantedChaosSpecifications(ITestOutputHelper output)
    {
        _output = output;
    }

    private MultiTenantedMessageStore _store;

    protected async Task execute<TScript>(TransportConfiguration configuration)
        where TScript : ChaosScript, new()
    {
        var database = new MultiDatabaseMartenStorageStrategy();
        await database.InitializeAsync();

        using var driver = new ChaosDriver(_output, database, configuration);
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
        execute<Simplistic>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_Simple() =>
        execute<Simplistic>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_Simple() =>
        execute<Simplistic>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_Simple() =>
        execute<Simplistic>(RabbitMqFiveBufferedListeners);


    [Fact]
    public Task RabbitMqDurableListener_Marten_Simple() =>
        execute<Simplistic>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_Simple() =>
        execute<Simplistic>(RabbitMqFiveDurableListeners);

    [Fact]
    public Task RabbitMqOneInlineListener_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqFiveBufferedListeners);


    [Fact]
    public Task RabbitMqDurableListener_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_ReceiverStartsLater() =>
        execute<ReceiverStartsLater>(RabbitMqFiveDurableListeners);

    [Fact]
    public Task RabbitMqOneInlineListener_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqOneInlineListener);

    [Fact]
    public Task RabbitMqFiveParallelInlineListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqFiveParallelInlineListeners);

    [Fact]
    public Task RabbitMqBufferedListener_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqBufferedListener);

    [Fact]
    public Task RabbitMqFiveBufferedListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqFiveBufferedListeners);

    [Fact]
    public Task RabbitMqDurableListener_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqDurableListener);

    [Fact]
    public Task RabbitMqFiveDurableListeners_Marten_ReceiverGoesUpAndDown() =>
        execute<ReceiverGoesUpAndDown>(RabbitMqFiveDurableListeners);
}