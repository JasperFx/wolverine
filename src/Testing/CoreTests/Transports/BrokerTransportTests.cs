using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

public class BrokerTransportTests
{
    [Fact]
    public void maybe_correct_name_does_nothing_without_a_prefix()
    {
        var transport = new FakeTransport();
        transport.MaybeCorrectName("foo")
            .ShouldBe("foo");
    }

    [Fact]
    public void maybe_correct_name_does_sanitization_at_least()
    {
        var transport = new FakeTransport();
        transport.MaybeCorrectName("foo#bar")
            .ShouldBe("foo.bar");
    }

    [Fact]
    public void maybe_correct_name_with_identifier()
    {
        var transport = new FakeTransport();
        transport.IdentifierPrefix = "me";
        
        transport.MaybeCorrectName("foo")
            .ShouldBe("me~foo");
    }
}

public class FakeTransport : BrokerTransport<FakeEndpoint>
{
    public FakeTransport() : base("fake", "Fake")
    {
        IdentifierDelimiter = "~";
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace("#", ".");
    }

    protected override IEnumerable<FakeEndpoint> endpoints()
    {
        throw new NotImplementedException();
    }

    protected override FakeEndpoint findEndpointByUri(Uri uri)
    {
        throw new NotImplementedException();
    }

    public override ValueTask ConnectAsync(IWolverineRuntime logger)
    {
        throw new NotImplementedException();
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        throw new NotImplementedException();
    }
}

public class FakeEndpoint : Endpoint, IBrokerEndpoint
{
    public FakeEndpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public ValueTask TeardownAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }
}