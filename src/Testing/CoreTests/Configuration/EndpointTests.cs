using JasperFx.Core;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

public class EndpointTests
{
    [Fact]
    public void sharding_by_group_id_is_not_enabled_by_default()
    {
        new TestEndpoint(EndpointRole.Application).GroupShardingSlotNumber.ShouldBeNull();
    }
    
    [Fact]
    public void telemetry_is_enabled_by_default()
    {
        new TestEndpoint(EndpointRole.System).TelemetryEnabled.ShouldBeTrue();
        new TestEndpoint(EndpointRole.Application).TelemetryEnabled.ShouldBeTrue();
    }

    [Fact]
    public void listener_scope_is_competing_by_default()
    {
        new TestEndpoint(EndpointRole.System)
            .ListenerScope.ShouldBe(ListenerScope.CompetingConsumers);
    }

    [Theory]
    [InlineData(true, ListenerScope.CompetingConsumers, DurabilityMode.Solo, true)]
    [InlineData(false, ListenerScope.CompetingConsumers, DurabilityMode.Solo, false)]
    [InlineData(true, ListenerScope.CompetingConsumers, DurabilityMode.Balanced, true)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Balanced, false)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Solo, true)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.Serverless, false)]
    [InlineData(true, ListenerScope.Exclusive, DurabilityMode.MediatorOnly, false)]
    [InlineData(false, ListenerScope.CompetingConsumers, DurabilityMode.Balanced, false)]
    public void should_auto_start_as_listener(bool isListener, ListenerScope scope, DurabilityMode mode, bool shouldStart)
    {
        var endpoint = new TestEndpoint(EndpointRole.System){IsListener = isListener, ListenerScope = scope};
        var settings = new DurabilitySettings { Mode = mode };

        endpoint.ShouldAutoStartAsListener(settings).ShouldBe(shouldStart);
    }

    [Fact]
    public void return_message_type_rule()
    {
        var endpoint = new TestEndpoint(EndpointRole.System);
        endpoint.RulesForIncoming().Any().ShouldBeFalse();

        endpoint.MessageType = typeof(Message1);

        var rules = endpoint.RulesForIncoming().ToArray();

        var envelope = ObjectMother.Envelope();
        foreach (var rule in rules)
        {
            rule.Modify(envelope);
        }
        
        envelope.MessageType.ShouldBe(typeof(Message1).ToMessageTypeName());
    }
    
    [Fact]
    public void return_tenant_id_rule()
    {
        var endpoint = new TestEndpoint(EndpointRole.System);
        endpoint.RulesForIncoming().Any().ShouldBeFalse();

        endpoint.TenantId = "one";

        var rules = endpoint.RulesForIncoming().ToArray();

        var envelope = ObjectMother.Envelope();
        foreach (var rule in rules)
        {
            rule.Modify(envelope);
        }
        
        envelope.TenantId.ShouldBe("one");
    }

    [Fact]
    public void maybe_wrap_receiver_no_rules()
    {
        var endpoint = new TestEndpoint(EndpointRole.System);
        endpoint.RulesForIncoming().Any().ShouldBeFalse();

        var inner = Substitute.For<IReceiver>();
        
        endpoint.MaybeWrapReceiver(inner)
            .ShouldBeSameAs(inner);
    }

    [Fact]
    public void describe_properties_reports_the_failure_count_before_circuit_breaks()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application)
        {
            FailuresBeforeCircuitBreaks = 7,
            PingIntervalForCircuitResume = 9.Seconds()
        };

        var properties = endpoint.DescribeProperties();

        properties[nameof(Endpoint.FailuresBeforeCircuitBreaks)].ShouldBe(7);
        properties[nameof(Endpoint.PingIntervalForCircuitResume)].ShouldBe(9.Seconds());
    }

    [Theory]
    [InlineData(EndpointMode.BufferedInMemory)]
    [InlineData(EndpointMode.Durable)]
    public void describe_properties_includes_buffering_limits_when_back_pressure_applies(EndpointMode mode)
    {
        var endpoint = new TestEndpoint(EndpointRole.Application)
        {
            Mode = mode,
            BufferingLimits = new BufferingLimits(2000, 800)
        };

        var properties = endpoint.DescribeProperties();

        properties["BufferingLimits.Maximum"].ShouldBe(2000);
        properties["BufferingLimits.Restart"].ShouldBe(800);
    }

    [Fact]
    public void describe_properties_omits_buffering_limits_for_inline_endpoints()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application)
        {
            SupportsInlineListeners = true,
            Mode = EndpointMode.Inline
        };

        var properties = endpoint.DescribeProperties();

        properties.ContainsKey("BufferingLimits.Maximum").ShouldBeFalse();
        properties.ContainsKey("BufferingLimits.Restart").ShouldBeFalse();
    }

    [Fact]
    public void describe_properties_includes_circuit_breaker_options_when_configured()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application)
        {
            CircuitBreakerOptions = new CircuitBreakerOptions
            {
                FailurePercentageThreshold = 40,
                PauseTime = 2.Minutes()
            }
        };

        var properties = endpoint.DescribeProperties();

        properties["CircuitBreakerOptions.FailurePercentageThreshold"].ShouldBe(40);
        properties["CircuitBreakerOptions.PauseTime"].ShouldBe(2.Minutes());
    }

    [Fact]
    public void describe_properties_omits_circuit_breaker_options_when_not_configured()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application);
        endpoint.CircuitBreakerOptions.ShouldBeNull();

        var properties = endpoint.DescribeProperties();

        properties.ContainsKey("CircuitBreakerOptions.FailurePercentageThreshold").ShouldBeFalse();
        properties.ContainsKey("CircuitBreakerOptions.PauseTime").ShouldBeFalse();
    }

    [Fact]
    public void maybe_wrap_receiver_with_rules()
    {
        var endpoint = new TestEndpoint(EndpointRole.System);
        endpoint.TenantId = "one";
        
        var inner = Substitute.For<IReceiver>();

        var wrapped = endpoint.MaybeWrapReceiver(inner)
            .ShouldBeOfType<ReceiverWithRules>();
        
        wrapped.Inner.ShouldBeSameAs(inner);
        wrapped.Rules.Single().ShouldBeOfType<TenantIdRule>();

    }
}

public class TestEndpoint : Endpoint
{
    public TestEndpoint(EndpointRole role) : base(new Uri("stub://one"), role)
    {
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public bool SupportsInlineListeners { get; set; }

    protected override bool supportsMode(EndpointMode mode)
    {
        if (mode == EndpointMode.Inline) return SupportsInlineListeners;
        return true;
    }
}