using Shouldly;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// Contract-level sanity checks for <see cref="IBrokerHealthProbe"/>,
/// <see cref="BrokerHealthSnapshot"/>, and <see cref="BrokerHealthStatus"/>. These
/// don't exercise any real transport -- they just guarantee the public surface
/// stays stable for downstream consumers (e.g. CritterWatch).
/// </summary>
public class IBrokerHealthProbe_contract_tests
{
    [Fact]
    public void enum_has_expected_members()
    {
        Enum.GetNames<BrokerHealthStatus>()
            .ShouldBe(new[]
            {
                nameof(BrokerHealthStatus.Unknown),
                nameof(BrokerHealthStatus.Healthy),
                nameof(BrokerHealthStatus.Degraded),
                nameof(BrokerHealthStatus.Unhealthy)
            }, ignoreOrder: true);
    }

    [Fact]
    public void snapshot_is_a_value_record()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new BrokerHealthSnapshot(
            new Uri("rabbitmq://example/vhost"),
            "RabbitMQ",
            BrokerHealthStatus.Healthy,
            "Connected",
            "2099-01-01T00:00:00.0000000+00:00",
            3,
            now);

        var b = a with { ReconnectAttempts = 4 };

        a.ShouldNotBe(b);
        a.ReconnectAttempts.ShouldBe(3);
        b.ReconnectAttempts.ShouldBe(4);

        // Records compare by value
        var aPrime = new BrokerHealthSnapshot(
            new Uri("rabbitmq://example/vhost"),
            "RabbitMQ",
            BrokerHealthStatus.Healthy,
            "Connected",
            "2099-01-01T00:00:00.0000000+00:00",
            3,
            now);

        a.ShouldBe(aPrime);
    }

    [Fact]
    public async Task probes_can_be_implemented_and_invoked()
    {
        IBrokerHealthProbe probe = new FakeBrokerHealthProbe();
        var snapshot = await probe.ProbeAsync(CancellationToken.None);

        snapshot.TransportType.ShouldBe("Fake");
        snapshot.Status.ShouldBe(BrokerHealthStatus.Healthy);
        snapshot.ReconnectAttempts.ShouldBe(0);
    }

    private sealed class FakeBrokerHealthProbe : IBrokerHealthProbe
    {
        public Task<BrokerHealthSnapshot> ProbeAsync(CancellationToken ct)
        {
            return Task.FromResult(new BrokerHealthSnapshot(
                new Uri("fake://broker"),
                "Fake",
                BrokerHealthStatus.Healthy,
                Description: null,
                CertificateExpiry: null,
                ReconnectAttempts: 0,
                LastSuccessfulAt: DateTimeOffset.UtcNow));
        }
    }
}
