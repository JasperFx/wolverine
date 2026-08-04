using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;
namespace Wolverine.MQTT.Tests;

// GH-3763: costs one retry in CIMQTT5, accepted on purpose. Deliberately NOT tagged Category=Flaky --
// a tag would stop it running, and a test that runs and occasionally retries is worth more than a test
// that runs nowhere. This note exists so the next person does not repeat the search below.
//
// Symptom, from the retry ledger (GH-3787, main run 30856898284):
//
//     MQTTnet.Exceptions.MqttCommunicationException : Broken pipe
//       ---- System.Net.Sockets.SocketException : Broken pipe
//
// broadcast_to_topic_by_user_logic.route_by_derived_topics_2 failed with the identical error in the
// same run, so whatever this is, it is not specific to either class.
//
// Three candidates were tested and eliminated. Do not re-run these:
//
//   - Teardown ordering. DisposeAsync below stops the Broker BEFORE the two hosts still connected to
//     it, and 7 of the 12 broker-using classes in this project share that shape. It looks like the
//     obvious culprit and it is not: a faithful reproduction -- sender and receiver hosts, a real
//     tracked broadcast through the broker, then broker-first teardown -- throws nothing in 10 runs,
//     in either ordering.
//   - A port collision between Bobcat worker processes. PortFinder.GetAvailablePort() is a genuine
//     TOCTOU: it binds port 0, reads the port, closes the socket, and the broker binds later. But a
//     collision cannot produce this error -- MQTTnet throws SocketException "Address already in use",
//     which fails the whole class at InitializeAsync rather than breaking a pipe mid-test.
//   - Local reproduction under concurrency. Six concurrent full-suite runs (three processes, twice)
//     produced no occurrence at all.
//
// What is left standing is a keep-alive drop under CI load, which needs a call site to confirm. The
// ledger now records the failing attempt's stack (GH-3763, PR #3811), so the next occurrence writes it
// into the test-ledger-CIMQTT5 artifact. Start there, not from scratch.
//
// Worth weighing before spending more on it: this is one retry, it has never turned main red, and two
// rounds of investigation have already gone into it.
[Collection("acceptance")]
public class listen_with_topic_wildcards : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender = null!;
    private IHost _receiver = null!;

    public listen_with_topic_wildcards(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        var port = PortFinder.GetAvailablePort();


        Broker = new LocalMqttBroker(port)
        {
            Logger = new XUnitLogger( _output, "MQTT")
        };

        await Broker.StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();

        #region sample_listen_to_mqtt_topic_filter
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqttWithLocalBroker(port);
                opts.ListenToMqttTopic("incoming/#").RetainMessages();
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task broadcast()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming/one", new ColorMessage("blue")));

        var received = session.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("blue");
    }

    public LocalMqttBroker Broker { get; set; } = null!;

    public async ValueTask DisposeAsync()
    {
        await Broker.StopAsync();
        await Broker.DisposeAsync();
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }
}