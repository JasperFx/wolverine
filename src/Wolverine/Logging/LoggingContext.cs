using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Logging;

public class LoggingContext : Dictionary<string, object>
{
    public void AddRange(params (string, object)[] kvp)
    {
        foreach (var (key, value) in kvp)
        {
            TryAdd(key, value);
        }
    }
}

public class LoggingContextFrame : SyncFrame
{
    private readonly Variable _loggingContext;
    public LoggingContextFrame()
    {
        _loggingContext = Create<LoggingContext>("wolverineLoggingContext_loggingTools");
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _loggingContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {_loggingContext.Usage} = new {typeof(LoggingContext).FullName}();");
        Next?.GenerateCode(method, writer);
    }
}

public class AddConstantsToLoggingContextFrame : SyncFrame
{
    private readonly (string, object)[] _loggingConstants;
    private Variable _loggingContext;
    public AddConstantsToLoggingContextFrame(params (string, object)[] loggingConstants)
    {
        if (loggingConstants.Any(x => x.Item2 is not string && !x.Item2.GetType().IsNumeric()))
        {
            throw new ArgumentException("One or more of the constants provided is not a string or a numeric type", nameof(loggingConstants));
        }
        _loggingConstants = loggingConstants;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _loggingContext = chain.FindVariable(typeof(LoggingContext));
        yield break;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_loggingContext!.Usage}.{nameof(LoggingContext.AddRange)}({string.Join(", ", _loggingConstants.Select(kvp => $"(\"{kvp.Item1}\", {GetValue(kvp.Item2)})"))});");
        foreach (var (key, value) in _loggingConstants)
        {
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{key.ToTelemetryFriendly()}\", {GetValue(value)});");
        }
        Next?.GenerateCode(method, writer);
        return;

        static string GetValue(object o) => o is string ? $"\"{o}\"" : o.ToString()!;
    }
}

internal static class Extensions
{
    public static void AddMiddlewareAfterLoggingContextFrame(this IChain chain, params Frame[] frames)
    {
        var frameIndex = chain.Middleware.FindIndex(f => f is LoggingContextFrame);
        if (frameIndex == -1)
        {
            frameIndex = 0;
            chain.Middleware.Insert(frameIndex, new LoggingContextFrame());
        }
        chain.Middleware.InsertRange(frameIndex + 1, frames);
    }
}