using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-5342. An externally owned endpoint belongs to another system, so the broker resource must never create it in
/// <c>resources setup</c> or delete it in <c>resources teardown</c>, while it still checks that it exists.
/// </summary>
public class externally_owned_broker_endpoints_5342
{
    private readonly RecordingTransport theTransport = new();
    private readonly BrokerResource theResource;

    public externally_owned_broker_endpoints_5342()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions { ServiceName = "externally-owned" });
        runtime.Logger.Returns(NullLogger.Instance);

        theResource = new BrokerResource(theTransport, runtime);
    }

    [Fact]
    public void endpoints_are_not_externally_owned_by_default()
    {
        new RecordingEndpoint("owned").IsExternallyOwned.ShouldBeFalse();
    }

    [Fact]
    public void should_auto_provision_needs_auto_provision_and_an_owned_endpoint()
    {
        var owned = theTransport.Add("owned");
        var external = theTransport.Add("external", externallyOwned: true);

        theTransport.ShouldAutoProvision(owned).ShouldBeFalse();
        theTransport.ShouldAutoProvision(external).ShouldBeFalse();

        theTransport.AutoProvision = true;

        theTransport.ShouldAutoProvision(owned).ShouldBeTrue();
        theTransport.ShouldAutoProvision(external).ShouldBeFalse();
    }

    [Fact]
    public async Task setup_skips_externally_owned_endpoints()
    {
        var owned = theTransport.Add("owned");
        var external = theTransport.Add("external", externallyOwned: true);

        await theResource.Setup(CancellationToken.None);

        owned.SetupCount.ShouldBe(1);
        external.SetupCount.ShouldBe(0);
    }

    [Fact]
    public async Task teardown_skips_externally_owned_endpoints()
    {
        var owned = theTransport.Add("owned");
        var external = theTransport.Add("external", externallyOwned: true);

        await theResource.Teardown(CancellationToken.None);

        owned.TeardownCount.ShouldBe(1);
        external.TeardownCount.ShouldBe(0);
    }

    [Fact]
    public async Task check_still_covers_externally_owned_endpoints()
    {
        var owned = theTransport.Add("owned");
        var external = theTransport.Add("external", externallyOwned: true);

        await theResource.Check(CancellationToken.None);

        owned.CheckCount.ShouldBe(1);
        external.CheckCount.ShouldBe(1);
    }

    [Fact]
    public void externally_owned_shows_up_in_the_endpoint_description()
    {
        theTransport.Add("owned").DescribeProperties().ContainsKey(nameof(Endpoint.IsExternallyOwned)).ShouldBeFalse();

        theTransport.Add("external", externallyOwned: true).DescribeProperties()[nameof(Endpoint.IsExternallyOwned)]
            .ShouldBe(true);
    }

    public class RecordingTransport : BrokerTransport<RecordingEndpoint>
    {
        private readonly List<RecordingEndpoint> _endpoints = [];

        public RecordingTransport() : base("recording", "Recording", ["recording"])
        {
        }

        public RecordingEndpoint Add(string name, bool externallyOwned = false)
        {
            var endpoint = new RecordingEndpoint(name) { IsExternallyOwned = externallyOwned };
            _endpoints.Add(endpoint);
            return endpoint;
        }

        protected override IEnumerable<RecordingEndpoint> endpoints() => _endpoints;

        protected override RecordingEndpoint findEndpointByUri(Uri uri) => _endpoints.Single(x => x.Uri == uri);

        public override Uri ResourceUri { get; } = new("recording://transport");

        public override ValueTask ConnectAsync(IWolverineRuntime runtime) => ValueTask.CompletedTask;

        public override IEnumerable<PropertyColumn> DiagnosticColumns() => [];
    }

    public class RecordingEndpoint : Endpoint, IBrokerEndpoint
    {
        public RecordingEndpoint(string name) : base(new Uri($"recording://{name}"), EndpointRole.Application)
        {
        }

        public int SetupCount { get; private set; }
        public int TeardownCount { get; private set; }
        public int CheckCount { get; private set; }

        public ValueTask<bool> CheckAsync()
        {
            CheckCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask TeardownAsync(ILogger logger)
        {
            TeardownCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetupAsync(ILogger logger)
        {
            SetupCount++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            throw new NotSupportedException();
        }
    }
}
