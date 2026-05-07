using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling.Faults.Integration;

public class FaultRedactionIntegrationTests
{
    public record OrderPlaced(string OrderId);
    public record OtherMessage(int Id);

    public class AlwaysFailsHandler
    {
        public static Task Handle(OrderPlaced _) =>
            throw new InvalidOperationException("secret-canary-12345");

        public static Task Handle(OtherMessage _) =>
            throw new InvalidOperationException("other-message-canary");
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
    public async Task globally_redacted_exception_message_arrives_empty_on_collector()
    {
        var (host, collector) = await StartHostAsync(opts =>
            opts.PublishFaultEvents(includeExceptionMessage: false));
        try
        {
            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-redact-1"));

            var fault = collector.Order.ShouldHaveSingleItem();
            fault.Exception.Type.ShouldBe(typeof(InvalidOperationException).FullName);
            fault.Exception.Message.ShouldBe(string.Empty);
            fault.Exception.StackTrace.ShouldNotBeNull();
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task per_type_redaction_overrides_global()
    {
        var (host, collector) = await StartHostAsync(opts =>
        {
            // Global: full detail (defaults). Per-type: redact OrderPlaced only.
            opts.PublishFaultEvents();
            opts.Policies.ForMessagesOfType<OrderPlaced>()
                .PublishFault(includeExceptionMessage: false);
        });
        try
        {
            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OrderPlaced("o-redact-2"));
            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .PublishMessageAndWaitAsync(new OtherMessage(7));

            var orderFault = collector.Order.ShouldHaveSingleItem();
            orderFault.Exception.Message.ShouldBe(string.Empty);

            var otherFault = collector.Other.ShouldHaveSingleItem();
            otherFault.Exception.Message.ShouldBe("other-message-canary");
        }
        finally { await host.StopAsync(); }
    }
}
