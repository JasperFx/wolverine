using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.ErrorHandling;

public class ListenerCircuitBreakerCountsAttemptsOnce
{
    [Fact]
    public async Task PausesAfterFiveFailuresButNotAfterFour()
    {
        // Arrange
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<BreakerProbeHandler>();
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var endpoint = new StubEndpoint("breaker-attempts", new StubTransport())
        {
            IsListener = true,
            Mode = EndpointMode.BufferedInMemory,
            MaxDegreeOfParallelism = 1,
            CircuitBreakerOptions = new CircuitBreakerOptions
            {
                MinimumThreshold = 5,
                FailurePercentageThreshold = 50,
                TrackingPeriod = 4.Minutes(),
                PauseTime = 1.Minutes()
            }
        };
        endpoint.Compile(runtime);
        await using var agent = new ListeningAgent(endpoint, runtime);
        await agent.StartAsync();
        var generation = agent.CircuitBreaker!.DetermineGeneration(DateTimeOffset.UtcNow);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(10.Seconds());

        // Act
        await agent.EnqueueDirectlyAsync(Enumerable.Range(1, 4).Select(_ => failingEnvelope()));
        while (generation.Total < 4)
        {
            await Task.Delay(10.Milliseconds(), timeout.Token);
        }

        // Assert
        generation.Total.ShouldBe(4);
        generation.Failures.ShouldBe(4);
        agent.Status.ShouldBe(ListeningStatus.Accepting);

        // Act
        await agent.EnqueueDirectlyAsync([failingEnvelope()]);
        while (agent.Status != ListeningStatus.Paused)
        {
            await Task.Delay(10.Milliseconds(), timeout.Token);
        }

        // Assert
        generation.Total.ShouldBe(5);
        generation.Failures.ShouldBe(5);
    }

    private static Envelope failingEnvelope()
    {
        return new Envelope(new BreakerProbeMessage())
        {
            MessageType = typeof(BreakerProbeMessage).ToMessageTypeName()
        };
    }
}

public record BreakerProbeMessage;

public class BreakerProbeHandler
{
    public static void Handle(BreakerProbeMessage message) => throw new InvalidOperationException("Expected test failure");
}
