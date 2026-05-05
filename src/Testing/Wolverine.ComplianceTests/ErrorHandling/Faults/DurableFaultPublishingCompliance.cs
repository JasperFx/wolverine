using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

public record DurableSnapshot(int DeadLetterRowCount, int OutgoingFaultRowCount);

public abstract class DurableFaultPublishingCompliance : IAsyncLifetime
{
    /// <summary>
    /// Configure the receiver-side host. Setup MUST:
    ///  - Register durable persistence with a dedicated schema/database for this suite.
    ///  - Register AlwaysFailsHandler so OrderPlaced terminally fails after one attempt.
    ///  - Set OnException&lt;Exception&gt;().MoveToErrorQueue() (no retries).
    ///  - Call PublishFaultEvents() globally.
    ///  - Apply optionalCompose before StartAsync so individual tests can inject decorators.
    /// </summary>
    public abstract Task<IHost> BuildCleanHostAsync(Action<WolverineOptions>? optionalCompose = null);

    /// <summary>
    /// Snapshot the persisted DLQ row count and the outgoing-fault-envelope row count
    /// from the receiver's durable store. Used to verify atomic commit / rollback.
    /// </summary>
    protected abstract Task<DurableSnapshot> SnapshotAsync(IHost host);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task smoke_durable_terminal_failure_publishes_fault()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            var session = await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .PublishMessageAndWaitAsync(new OrderPlaced("smoke-1"));

            var fault = session.AutoFaultsPublished
                .MessagesOf<Fault<OrderPlaced>>()
                .Single();
            fault.Message.OrderId.ShouldBe("smoke-1");

            var record = session.AutoFaultsPublished
                .Envelopes()
                .Single(e => e.Message is Fault<OrderPlaced>);
            record.Headers[FaultHeaders.AutoPublished].ShouldBe("true");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task atomic_dlq_and_fault_commit_together_on_success()
    {
        var host = await BuildCleanHostAsync();
        try
        {
            await host.TrackActivity()
                .DoNotAssertOnExceptionsDetected()
                .Timeout(30.Seconds())
                .PublishMessageAndWaitAsync(new OrderPlaced("atom-1"));

            var snapshot = await SnapshotAsync(host);
            snapshot.DeadLetterRowCount.ShouldBe(1);
            snapshot.OutgoingFaultRowCount.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task atomic_rollback_when_post_dlq_step_throws()
    {
        var host = await BuildCleanHostAsync(opts =>
        {
            opts.Services.AddSingleton<IFaultPublisher>(sp =>
            {
                var options = sp.GetRequiredService<WolverineOptions>();
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var runtime = sp.GetRequiredService<IWolverineRuntime>();
                var inner = new FaultPublisher(
                    options.FindOrCreateFaultPublishingPolicy(),
                    loggerFactory.CreateLogger<FaultPublisher>(),
                    runtime.Meter);
                return new CrashingFaultPublisherDecorator(inner);
            });
        });

        try
        {
            try
            {
                await host.TrackActivity()
                    .DoNotAssertOnExceptionsDetected()
                    .Timeout(15.Seconds())
                    .PublishMessageAndWaitAsync(new OrderPlaced("atom-2"));
            }
            catch
            {
                // Tracking timeout or simulated crash — acceptable; assertion is on durable state.
            }

            var snapshot = await SnapshotAsync(host);
            snapshot.DeadLetterRowCount.ShouldBe(0);
            snapshot.OutgoingFaultRowCount.ShouldBe(0);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
