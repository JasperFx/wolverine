using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

public record FileAdded(string FileName);

public class RandomFileChangeForPublish
{
    private readonly IMessageBus _messageBus;

    public RandomFileChangeForPublish(
        IMessageBus messageBus
    ) => _messageBus = messageBus;

    public async Task SimulateRandomFileChange()
    {
        await Task.Delay(
            TimeSpan.FromMilliseconds(
                new Random().Next(100, 1000)
            )
        );
        var randomFileName = Path.GetRandomFileName();
        await _messageBus.PublishAsync(new FileAdded(randomFileName));
    }
}


public class When_session_is_tracked_for_published_message_without_handler : IAsyncLifetime
{
    private readonly ITestOutputHelper _testOutputHelper;
    private IHost _host;

    public When_session_is_tracked_for_published_message_without_handler(
        ITestOutputHelper testOutputHelper
    ) => _testOutputHelper = testOutputHelper;

    public async Task InitializeAsync()
    {
        var hostBuilder = Host.CreateDefaultBuilder();
        hostBuilder.ConfigureServices(
            services => { services.AddSingleton<RandomFileChangeForPublish>(); }
        );
        hostBuilder.UseWolverine();

        _host = await hostBuilder.StartAsync();
    }

    [Fact]
    public async Task should_be_included_in_sent_record_collection()
    {
        var randomEventEmitter = _host.Services.GetRequiredService<RandomFileChangeForPublish>();

        var session = await _host
            .TrackActivity()
            .Timeout(2.Seconds())
            .ExecuteAndWaitAsync(
                (Func<IMessageContext, Task>)(
                    async (
                        _
                    ) => await randomEventEmitter.SimulateRandomFileChange()
                )
            );


        session.Sent.AllMessages()
            .Count()
            .ShouldBe(1);

        session.Sent.AllMessages()
            .First()
            .ShouldBeOfType<FileAdded>();
    }


    public async Task DisposeAsync() => await _host.StopAsync();
}
