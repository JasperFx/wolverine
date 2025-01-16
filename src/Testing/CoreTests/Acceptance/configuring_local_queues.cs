using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Acceptance;

public class configuring_local_queues : IntegrationContext
{
    public configuring_local_queues(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void apply_to_normal_non_sticky_default_routed_handler()
    {
        var runtime = Host.GetRuntime();
        runtime.Endpoints.EndpointByName("Frank")
            .ShouldBeOfType<LocalQueue>().Uri.ShouldBe(new Uri("local://coretests.acceptance.simplemessage/"));
    }

    [Fact]
    public void apply_to_sticky_handlers()
    {
        var runtime = Host.GetRuntime();
        runtime.Endpoints.EndpointByName("blue")
            .ShouldBeOfType<LocalQueue>().ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1);
        
        runtime.Endpoints.EndpointByName("green")
            .ShouldBeOfType<LocalQueue>().ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1000);
    }

    [Fact]
    public async Task use_with_separated_mode()
    {
        using var host = await new HostBuilder().UseWolverine(opts =>
        {
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
        }).StartAsync();
        
        var runtime = host.GetRuntime();
        runtime.Endpoints.EndpointByName(typeof(MultipleMessage1Handler).FullNameInCode().ToLowerInvariant())
            .ShouldBeOfType<LocalQueue>().ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1);
        
        runtime.Endpoints.EndpointByName(typeof(MultipleMessage2Handler).FullNameInCode().ToLowerInvariant())
            .ShouldBeOfType<LocalQueue>().ExecutionOptions.MaxDegreeOfParallelism.ShouldBe(1000);
    }
}

public record SimpleMessage;

public class SimpleMessageHandler : IConfigureLocalQueue
{
    public static void Configure(LocalQueueConfiguration configuration)
    {
        // Just got to do something to prove out the configuration
        configuration.Named("Frank");
    }
    
    public static void Handle(SimpleMessage message)
    {
        
    }
}

public record StuckMessage;

[StickyHandler("blue")]
public class BlueStuckMessageHandler : IConfigureLocalQueue
{
    public static void Configure(LocalQueueConfiguration configuration)
    {
        configuration.Sequential();
    }

    public static void Handle(StuckMessage message)
    {
        
    }
}

[StickyHandler("green")]
public class GreenStuckMessageHandler : IConfigureLocalQueue
{
    public static void Configure(LocalQueueConfiguration configuration)
    {
        configuration.MaximumParallelMessages(1000);
    }

    public static void Handle(StuckMessage message)
    {
        
    }
}

public record MultipleMessage;

#region sample_using_IConfigureLocalQueue

public class MultipleMessage1Handler : IConfigureLocalQueue
{
    public static void Handle(MultipleMessage message)
    {
        
    }

    // This method is configuring the local queue that executes this
    // handler to be strictly ordered
    public static void Configure(LocalQueueConfiguration configuration)
    {
        configuration.Sequential();
    }
}

#endregion

public class MultipleMessage2Handler : IConfigureLocalQueue
{
    public static void Handle(MultipleMessage message)
    {
        
    }

    public static void Configure(LocalQueueConfiguration configuration)
    {
        configuration.MaximumParallelMessages(1000);
    }
}


