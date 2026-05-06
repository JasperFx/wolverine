using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling.Faults.Integration;

public class PublishFaultEventsIntegrationTests
{
    public record OrderPlaced(string OrderId);
    public record OtherMessage(int Id);

    public class AlwaysFailsHandler
    {
        public static Task Handle(OrderPlaced _) =>
            throw new InvalidOperationException("synthetic order failure");

        public static Task Handle(OtherMessage _) =>
            throw new InvalidOperationException("synthetic other failure");
    }

    public class FaultCollector
    {
        public List<Fault<OrderPlaced>> Order { get; } = new();
        public List<Fault<OtherMessage>> Other { get; } = new();
    }

    public class FaultCollectorHandler
    {
        public Task Handle(Fault<OrderPlaced> f, FaultCollector collector)
        {
            collector.Order.Add(f);
            return Task.CompletedTask;
        }

        public Task Handle(Fault<OtherMessage> f, FaultCollector collector)
        {
            collector.Other.Add(f);
            return Task.CompletedTask;
        }
    }

    private static async Task<(IHost host, FaultCollector collector)> StartHostAsync(
        Action<WolverineOptions> configure)
    {
        var collector = new FaultCollector();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(collector);
                opts.OnException<Exception>().MoveToErrorQueue();
                configure(opts);
            })
            .StartAsync();
        return (host, collector);
    }

    [Fact]
    public async Task globally_enabled_publishes_fault_to_subscriber()
    {
        var (host, collector) = await StartHostAsync(opts => opts.PublishFaultEvents());
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-1"));

            session.AutoFaultsPublished
                .MessagesOf<Fault<OrderPlaced>>()
                .Single().Message.OrderId.ShouldBe("o-1");

            collector.Order.Single().Message.OrderId.ShouldBe("o-1");
            collector.Order[0].Exception.Type.ShouldBe(typeof(InvalidOperationException).FullName);
            collector.Order[0].Exception.Message.ShouldBe("synthetic order failure");
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task per_type_opt_in_publishes_fault_only_for_chosen_type()
    {
        var (host, collector) = await StartHostAsync(opts =>
        {
            // Global OFF; only OrderPlaced opted in.
            opts.Policies.ForMessagesOfType<OrderPlaced>().PublishFault();
        });
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OtherMessage(7));

            session.AutoFaultsPublished
                .MessagesOf<Fault<OtherMessage>>()
                .ShouldBeEmpty();
            collector.Other.ShouldBeEmpty();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task per_type_opt_out_overrides_global_on()
    {
        var (host, collector) = await StartHostAsync(opts =>
        {
            opts.PublishFaultEvents();
            opts.Policies.ForMessagesOfType<OrderPlaced>().DoNotPublishFault();
        });
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-2"));

            session.AutoFaultsPublished
                .MessagesOf<Fault<OrderPlaced>>()
                .ShouldBeEmpty();
            collector.Order.ShouldBeEmpty();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task discard_without_opt_in_does_not_publish()
    {
        var collector = new FaultCollector();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(collector);
                opts.OnException<Exception>().Discard();
                opts.PublishFaultEvents(); // DLQ-only — discards excluded
            })
            .StartAsync();
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-3"));

            session.AutoFaultsPublished.MessagesOf<Fault<OrderPlaced>>().ShouldBeEmpty();
            collector.Order.ShouldBeEmpty();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task discard_with_include_discarded_publishes()
    {
        var collector = new FaultCollector();
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(collector);
                opts.OnException<Exception>().Discard();
                opts.PublishFaultEvents(includeDiscarded: true);
            })
            .StartAsync();
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-4"));

            session.AutoFaultsPublished
                .MessagesOf<Fault<OrderPlaced>>()
                .Single().Message.OrderId.ShouldBe("o-4");
            collector.Order.Single().Message.OrderId.ShouldBe("o-4");

            collector.Order[0].Exception.Type
                .ShouldBe(typeof(InvalidOperationException).FullName);
            collector.Order[0].Exception.Message
                .ShouldBe("synthetic order failure");
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task fault_carries_auto_header_when_observed_by_subscriber()
    {
        var (host, _) = await StartHostAsync(opts => opts.PublishFaultEvents());
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-5"));

            var envelope = session.AutoFaultsPublished
                .Envelopes()
                .Single(e => e.Message is Fault<OrderPlaced>);

            envelope.Headers[FaultHeaders.AutoPublished].ShouldBe("true");
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task invoke_async_does_not_publish_fault()
    {
        var (host, collector) = await StartHostAsync(opts => opts.PublishFaultEvents());
        try
        {
            var bus = host.MessageBus();
            await Should.ThrowAsync<InvalidOperationException>(
                async () => await bus.InvokeAsync(new OrderPlaced("o-6")));

            collector.Order.ShouldBeEmpty();
        }
        finally { await host.StopAsync(); }
    }
}
