using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;

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
    }
}

public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new Stopwatch();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, Envelope envelope)
    {
        _stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds", envelope.Id, envelope.MessageType, _stopwatch.ElapsedMilliseconds);
    }
}

public static class UsingStopwatchMiddleware
{
    
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
        // This in effect turns into "I need ILogger<IChain> injected into the
        // compiled class"
        _logger = chain.FindVariable(typeof(ILogger<IChain>));
        yield return _logger;
    }
}

#endregion

#region sample_StopwatchAttribute

public class StopwatchAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IContainer container)
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