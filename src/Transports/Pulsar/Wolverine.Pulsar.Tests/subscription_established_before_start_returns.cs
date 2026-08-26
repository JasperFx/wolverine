using Shouldly;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-4149. DotPulsar's IConsumerBuilder.Create() returns as soon as the consumer object exists -- the
// Subscribe command goes to the broker on a background task. Wolverine did not wait for it, so
// IHost.StartAsync() returned while the topic did not yet exist at the broker: the admin API answered
// 404 for it at the instant start returned, five runs out of five.
//
// With SubscriptionInitialPosition defaulting to Latest, anything published into that window is not
// delivered to the subscription and is not redeliverable -- on a brand-new topic there is no earlier
// position to fall back to. It is silent message loss on first deployment, and it is what made the
// Pulsar suite drop exactly the first message it published under parallel load.
public class subscription_established_before_start_returns
{
    [Fact]
    public async Task the_subscription_exists_at_the_broker_when_start_returns()
    {
        using var http = new HttpClient();

        // Repeated because the defect is a race: on an idle machine the subscription often wins anyway.
        // The assertion is on the broker's own view, not on message delivery, so it holds either way.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var name = $"established-{Guid.NewGuid():N}";
            var subscription = "sub-" + Guid.NewGuid().ToString("N");

            using var host = await WolverineHost.ForAsync(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.ListenToPulsarTopic($"persistent://public/default/{name}")
                    .SubscriptionName(subscription);
            });

            var response = await http.GetAsync(
                $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/persistent/public/default/{name}/stats",
                TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.ShouldBeTrue(
                $"attempt {attempt}: the topic did not exist at the broker when StartAsync returned " +
                $"(admin API returned {(int)response.StatusCode})");

            var stats = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            stats.ShouldContain(subscription,
                customMessage: $"attempt {attempt}: the topic existed but the subscription was not established " +
                               "when StartAsync returned");
        }
    }
}
