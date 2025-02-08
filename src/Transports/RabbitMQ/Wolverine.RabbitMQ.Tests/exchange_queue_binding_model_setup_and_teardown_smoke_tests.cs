using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using JasperFx.Resources;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class exchange_queue_binding_model_setup_and_teardown_smoke_tests
{
    private readonly IStatefulResource theResource;
    private readonly RabbitMqTransport theTransport = new();

    public exchange_queue_binding_model_setup_and_teardown_smoke_tests()
    {
        theTransport.ConfigureFactory(f => f.HostName = "localhost");

        var expression = new RabbitMqTransportExpression(theTransport, new WolverineOptions());

        expression.DeclareExchange("direct1", exchange =>
        {
            exchange.IsDurable = true;
            exchange.ExchangeType = ExchangeType.Direct;
        });

        expression.DeclareExchange("fan1", exchange => { exchange.ExchangeType = ExchangeType.Fanout; });

        expression.DeclareQueue("xqueue1", q => q.TimeToLive(5.Minutes()));
        expression.DeclareQueue("xqueue2");

        expression
            .BindExchange("direct1")
            .ToQueue("xqueue1", "key1");

        expression
            .BindExchange("fan1")
            .ToQueue("xqueue2", "key2");

        var wolverineRuntime = Substitute.For<IWolverineRuntime>();
        wolverineRuntime.Logger.Returns(NullLogger.Instance);
        wolverineRuntime.DurabilitySettings.Returns(new DurabilitySettings());
        theTransport.TryBuildStatefulResource(wolverineRuntime, out var resource);

        theResource = resource;
    }

    [Fact]
    public async Task resource_setup()
    {
        await theResource.Setup(CancellationToken.None);
    }

    [Fact]
    public async Task clear_state_as_resource()
    {
        await theResource.Setup(CancellationToken.None);
        await theResource.ClearState(CancellationToken.None);
    }

    [Fact]
    public async Task delete_all()
    {
        await theResource.Setup(CancellationToken.None);
        await theResource.Teardown(CancellationToken.None);
    }
}