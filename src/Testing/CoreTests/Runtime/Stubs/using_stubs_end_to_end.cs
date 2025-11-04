using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Runtime.Stubs;

public class using_stubs_end_to_end : IAsyncLifetime
{
    private IHost theSender;
    private IHost theReceiver;

    public async Task InitializeAsync()
    {
        await SharedMemoryQueueManager.ClearAllAsync();
        
        theSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StubMessage4Handler));
                opts.UseSharedMemoryQueueing();

                opts.PublishAllMessages().ToSharedMemoryTopic("remote");
                opts.ListenToSharedMemorySubscription("sender", "replies").UseForReplies();


            }).StartAsync();

        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenToSharedMemorySubscription("remote", "receiver");
            }).StartAsync();

    }

    public async Task DisposeAsync()
    {
        await theSender.StopAsync();
        await theReceiver.StopAsync();
    }

    [Fact]
    public async Task baseline_state()
    {
        var bus = theSender.MessageBus();
        var response = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response.Id.ShouldBe("green");
    }

    [Fact]
    public async Task stub_single_message()
    {
        theSender.StubHandlers(stubs =>
        {
            stubs.Stub<StubMessage1, StubResponse1>(m => new StubResponse1(m.Id + "-1"));
            
        });
        
        var bus = theSender.MessageBus();
        var response = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response.Id.ShouldBe("green-1");
        
        var response2 = await bus.InvokeAsync<StubResponse2>(new StubMessage2("green"));
        response2.Id.ShouldBe("green");
    }

    [Fact]
    public async Task clear_all_reverts_back_to_normal()
    {
        theSender.StubMessageHandler<StubMessage1, StubResponse1>(m => new StubResponse1(m.Id + "-1"));
        
        theSender.ClearAllStubHandlers();
        
        var bus = theSender.MessageBus();
        var response = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response.Id.ShouldBe("green");
    }
    
    [Fact]
    public async Task clear_specific_reverts_back_to_normal()
    {
        theSender.StubMessageHandler<StubMessage1, StubResponse1>(m => new StubResponse1(m.Id + "-1"));

        theSender.StubHandlers(x => x.Clear<StubMessage1>());
        
        var bus = theSender.MessageBus();
        var response = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response.Id.ShouldBe("green");
    }

    [Fact]
    public async Task apply_second_stub_on_same_message_type()
    {
        theSender.StubMessageHandler<StubMessage1, StubResponse1>(m => new StubResponse1(m.Id + "-1"));

        var bus = theSender.MessageBus();
        var response = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response.Id.ShouldBe("green-1");
        
        theSender.StubMessageHandler<StubMessage1, StubResponse1>(m => new StubResponse1(m.Id + "-2"));
        
        var response2 = await bus.InvokeAsync<StubResponse1>(new StubMessage1("green"));
        response2.Id.ShouldBe("green-2");
    }
}

public record StubMessage1(string Id);
public record StubMessage2(string Id);
public record StubMessage3(string Id);
public record StubMessage4(string Id);

public record StubResponse1(string Id);
public record StubResponse2(string Id);
public record StubResponse3(string Id);
public record StubResponse4(string Id);

public static class StubMessage1Handler
{
    public static StubResponse1 Handle(StubMessage1 m) => new(m.Id);
}

public static class StubMessage2Handler
{
    public static StubResponse2 Handle(StubMessage2 m) => new(m.Id);
}

public static class StubMessage3Handler
{
    public static StubResponse3 Handle(StubMessage3 m) => new(m.Id);
}

public static class StubMessage4Handler
{
    public static StubResponse4 Handle(StubMessage4 m) => new(m.Id);
}