using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance;

public class logging_configuration : IntegrationContext
{
    public logging_configuration(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void wolverine_logging_attribute_impacts_handler_chain()
    {
        var chain = Handlers.HandlerFor<QuietMessage>().As<MessageHandler>()
            .Chain;

        chain.TelemetryEnabled.ShouldBeFalse();
        chain.SuccessLogLevel.ShouldBe(LogLevel.None);
        chain.ProcessingLogLevel.ShouldBe(LogLevel.Trace);
    }
}

#region sample_using_Wolverine_Logging_attribute

public class QuietMessage;

public class QuietMessageHandler
{
    [WolverineLogging(
        telemetryEnabled:false,
        successLogLevel: LogLevel.None,
        executionLogLevel:LogLevel.Trace)]
    public void Handle(QuietMessage message)
    {
        Console.WriteLine("Hush!");
    }
}

#endregion