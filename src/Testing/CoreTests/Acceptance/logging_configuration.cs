using System.Diagnostics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Logging;
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

    [Fact]
    public void set_log_message_activity_from_attribute_no_global_policy()
    {
        // Not configuration
        with(opts => { });

        chainFor<AuditedMessage3>().Middleware.OfType<LogStartingActivity>()
            .Single().Level.ShouldBe(LogLevel.Information);
    }
    
    [Fact]
    public void override_starting_message_activity_from_attribute_over_global_policy()
    {
        // Not configuration
        with(opts =>
        {
            opts.Policies.LogMessageStarting(LogLevel.Debug);
        });

        // Still Information!
        chainFor<AuditedMessage3>().Middleware.OfType<LogStartingActivity>()
            .Single().Level.ShouldBe(LogLevel.Information);
        
        // The default is still from the global policy
        chainFor<NormalMessage>().Middleware.OfType<LogStartingActivity>()
            .Single().Level.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public void override_the_log_start_messaging_to_off()
    {
        // Not configuration
        with(opts =>
        {
            opts.Policies.LogMessageStarting(LogLevel.Debug);
        });
        
        // Attribute says None, so it's None!!!
        chainFor<QuietMessage2>().Middleware.OfType<LogStartingActivity>()
            .Any().ShouldBeFalse();
    }
}

public record NormalMessage;

public static class NormalMessageHandler
{
    public static void Handle(NormalMessage m) => Debug.WriteLine("Got " + m);
}

#region sample_using_Wolverine_Logging_attribute

public record QuietMessage;

public record VerboseMessage;

public class QuietAndVerboseMessageHandler
{
    [WolverineLogging(
        telemetryEnabled:false,
        successLogLevel: LogLevel.None,
        executionLogLevel:LogLevel.Trace)]
    public void Handle(QuietMessage message)
    {
        Console.WriteLine("Hush!");
    }
    
    [WolverineLogging(
        // Enable Open Telemetry tracing
        TelemetryEnabled = true, 
        
        // Log on successful completion of this message
        SuccessLogLevel = LogLevel.Information, 
        
        // Log on execution being complete, but before Wolverine does its own book keeping
        ExecutionLogLevel = LogLevel.Information, 
        
        // Throw in yet another contextual logging statement
        // at the beginning of message execution
        MessageStartingLevel = LogLevel.Debug)]
    public void Handle(VerboseMessage message)
    {
        Console.WriteLine("Tell me about it!");
    }
}

#endregion

public record AuditedMessage3;

public record QuietMessage2;

public static class QuietMessage2Handler
{
    [WolverineLogging(MessageStartingLevel = LogLevel.None)]
    public static void Handle(QuietMessage2 m) => Debug.WriteLine("Got " + m);
}

public static class AuditedMessage3Handler
{
    [WolverineLogging(MessageStartingLevel = LogLevel.Information)]
    public static void Handle(AuditedMessage3 m) => Debug.WriteLine("Got " + m);
}