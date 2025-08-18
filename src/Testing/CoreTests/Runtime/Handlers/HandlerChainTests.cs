using JasperFx;
using Microsoft.Extensions.Logging;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class HandlerChainTests
{

    [Fact]
    public void the_default_log_level_is_information()
    {
        var chain = HandlerChain.For<Target>(x => x.Go(null), null);
        chain.ExecutionLogLevel.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void open_telemetry_enabled_is_true_by_default()
    {
        var chain = HandlerChain.For<Target>(x => x.Go(null), null);
        chain.TelemetryEnabled.ShouldBeTrue();
    }

    [Fact]
    public void create_by_method()
    {
        var chain = HandlerChain.For<Target>(x => x.Go(null), null);
        chain.MessageType.ShouldBe(typeof(Message1));

        var methodCall = chain.Handlers.Single();
        methodCall.HandlerType.ShouldBe(typeof(Target));
        methodCall.Method.Name.ShouldBe(nameof(Target.Go));
    }

    [Fact]
    public void create_by_static_method()
    {
        var chain = HandlerChain.For<Target>(nameof(Target.GoStatic), null);

        chain.MessageType.ShouldBe(typeof(Message2));

        var methodCall = chain.Handlers.Single();
        methodCall.HandlerType.ShouldBe(typeof(Target));
        methodCall.Method.Name.ShouldBe(nameof(Target.GoStatic));
    }

    [Fact]
    public void default_number_of_max_attempts_is_null()
    {
        var chain = HandlerChain.For<Target>(nameof(Target.GoStatic), null);
        chain.Failures.MaximumAttempts.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void ignore_message_type_as_service_dependency()
    {
        var chain = HandlerChain.For<Target>(nameof(Target.GoStatic), null);
        chain.ServiceDependencies(ServiceContainer.Empty(), new List<Type>())
            .ShouldNotContain(typeof(Message2));
    }

    public class Target
    {
        public void Go(Message1 message)
        {
        }

        public static void GoStatic(Message2 message)
        {
        }
    }
}