using System.Diagnostics;
using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace DocumentationSamples;

public class Middleware
{
    public static void Stopwatch(ILogger logger)
    {
        #region sample_stopwatch_concept

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        try
        {
            // execute the HTTP request
            // or message
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation("Ran something in " + stopwatch.ElapsedMilliseconds);
        }

        #endregion


        var middleware = new MiddlewareSample();

        #region sample_demonstrating_middleware_application

        middleware.Before();
        try
        {
            // call the actual handler methods
            middleware.After();
        }
        finally
        {
            middleware.Finally();
        }

        #endregion
    }
}

public class MiddlewareSample
{
    public void Before()
    {
    }

    public void After()
    {
    }

    public void Finally()
    {
    }
}

#region sample_StopwatchMiddleware_1

public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, Envelope envelope)
    {
        _stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds",
            envelope.Id, envelope.MessageType, _stopwatch.ElapsedMilliseconds);
    }
}

#endregion

public record PotentiallySlowMessage(string Name);

#region sample_apply_middleware_by_attribute

public static class SomeHandler
{
    [Middleware(typeof(StopwatchMiddleware))]
    public static void Handle(PotentiallySlowMessage message)
    {
        // do something expensive with the message
    }
}

#endregion

#region sample_silly_micro_optimized_stopwatch_middleware

public static class StopwatchMiddleware2
{
    // The Stopwatch being returned from this method will
    // be passed back into the later method
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stopwatch Before()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        return stopwatch;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Finally(Stopwatch stopwatch, ILogger logger, Envelope envelope)
    {
        stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds",
            envelope.Id, envelope.MessageType, stopwatch.ElapsedMilliseconds);
    }
}

#endregion

public static class UsingStopwatchMiddleware
{
    public static async Task apply()
    {
        #region sample_applying_middleware_by_policy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Apply our new middleware to message handlers, but optionally
                // filter it to only messages from a certain namespace
                opts.Policies
                    .AddMiddleware<StopwatchMiddleware>(chain =>
                        chain.MessageType.IsInNamespace("MyApp.Messages.Important"));
            }).StartAsync();

        #endregion
    }
}

#region sample_StopwatchFrame

public class StopwatchFrame : SyncFrame
{
    private readonly IChain _chain;
    private readonly Variable _stopwatch;
    private Variable _logger;

    public StopwatchFrame(IChain chain)
    {
        _chain = chain;

        // This frame creates a Stopwatch, so we
        // expose that fact to the rest of the generated method
        // just in case someone else wants that
        _stopwatch = new Variable(typeof(Stopwatch), "stopwatch", this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var stopwatch = new {typeof(Stopwatch).FullNameInCode()}();");
        writer.Write("stopwatch.Start();");

        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        // Write a finally block where you record the stopwatch
        writer.Write("BLOCK:finally");

        writer.Write("stopwatch.Stop();");
        writer.Write(
            $"{_logger.Usage}.Log(Microsoft.Extensions.Logging.LogLevel.Information, \"{_chain.Description} ran in \" + {_stopwatch.Usage}.{nameof(Stopwatch.ElapsedMilliseconds)});)");

        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // This in effect turns into "I need ILogger<message type> injected into the
        // compiled class"
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }
}

#endregion

#region sample_StopwatchAttribute

public class StopwatchAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.Middleware.Add(new StopwatchFrame(chain));
    }
}

#endregion

#region sample_ClockedEndpoint

public class ClockedEndpoint
{
    [Stopwatch]
    public string get_clocked()
    {
        return "how fast";
    }
}

#endregion