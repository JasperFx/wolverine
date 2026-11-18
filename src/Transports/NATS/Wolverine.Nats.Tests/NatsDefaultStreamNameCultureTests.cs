using System.Globalization;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The default JetStream stream name is derived from the subject by upper-casing it. That has to be culture
/// invariant: under tr-TR, "i".ToUpper() is "İ" (U+0130), so "orders.invoice" came out as "ORDERS_İNVOİCE" on a
/// Turkish host and as "ORDERS_INVOICE" everywhere else.
/// </summary>
public class NatsDefaultStreamNameCultureTests
{
    private static NatsEndpoint EndpointFor(string subject)
    {
        var transport = new NatsTransport();
        return (NatsEndpoint)transport.GetOrCreateEndpoint(NatsEndpointUri.Subject(subject));
    }

    private static void UnderTurkishCulture(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void listener_default_stream_name_is_culture_invariant()
    {
        var endpoint = EndpointFor("orders.invoice");

        UnderTurkishCulture(() =>
        {
            var configuration = new NatsListenerConfiguration(endpoint);
            configuration.UseJetStream();
            ((IDelayedEndpointConfiguration)configuration).Apply();
        });

        endpoint.StreamName.ShouldBe("ORDERS_INVOICE");
    }

    [Fact]
    public void subscriber_default_stream_name_is_culture_invariant()
    {
        var endpoint = EndpointFor("orders.invoice");

        UnderTurkishCulture(() =>
        {
            var configuration = new NatsSubscriberConfiguration(endpoint);
            configuration.UseJetStream();
            ((IDelayedEndpointConfiguration)configuration).Apply();
        });

        endpoint.StreamName.ShouldBe("ORDERS_INVOICE");
    }
}
