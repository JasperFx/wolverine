using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class when_using_handler_type_naming : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime _runtime;

    public when_using_handler_type_naming()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.UseAmazonSqsTransport()
                .UseConventionalRouting(NamingSource.FromHandlerType)
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(SqsHandlerTypeNamingHandler));
        });

        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    [Fact]
    public void listener_endpoint_should_be_named_after_handler_type()
    {
        // SQS sanitizes names by replacing dots with hyphens
        var expectedName = AmazonSqsTransport.SanitizeSqsName(typeof(SqsHandlerTypeNamingHandler).ToMessageTypeName());
        var transport = _runtime.Options.Transports.GetOrCreate<AmazonSqsTransport>();

        transport.Queues.Any(q => q.QueueName == expectedName)
            .ShouldBeTrue($"Expected queue named '{expectedName}' for handler type, but found: {string.Join(", ", transport.Queues.Select(q => q.QueueName))}");
    }

    [Fact]
    public void listener_should_be_active()
    {
        var expectedName = AmazonSqsTransport.SanitizeSqsName(typeof(SqsHandlerTypeNamingHandler).ToMessageTypeName());

        _runtime.Endpoints.ActiveListeners().Any(x => x.Uri.ToString().Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue($"Expected active listener containing '{expectedName}'");
    }

    public void Dispose()
    {
        _host.Dispose();
    }
}

public record SqsHandlerTypeNamingMessage;

public class SqsHandlerTypeNamingHandler
{
    public void Handle(SqsHandlerTypeNamingMessage message)
    {
    }
}
