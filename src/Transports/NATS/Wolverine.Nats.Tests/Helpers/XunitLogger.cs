using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests.Helpers;

/// <summary>
/// Logger provider that writes to xUnit test output
/// </summary>
public class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;
    
    public XunitLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }
    
    public ILogger CreateLogger(string categoryName)
    {
        return new XunitLogger(categoryName, _output);
    }
    
    public void Dispose() { }
}

/// <summary>
/// Logger that writes to xUnit test output
/// </summary>
public class XunitLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ITestOutputHelper _output;
    
    public XunitLogger(string categoryName, ITestOutputHelper output)
    {
        _categoryName = categoryName;
        _output = output;
    }
    
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }
    
    public bool IsEnabled(LogLevel logLevel) => true;
    
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var message = formatter(state, exception);
        
        // Short category name for readability
        var shortCategory = _categoryName.Contains('.') 
            ? _categoryName.Substring(_categoryName.LastIndexOf('.') + 1) 
            : _categoryName;
        
        _output.WriteLine($"[{timestamp}] [{logLevel,-5}] {shortCategory}: {message}");
        
        if (exception != null)
        {
            _output.WriteLine($"Exception: {exception.GetType().Name}: {exception.Message}");
            _output.WriteLine(exception.StackTrace);
        }
    }
}

/// <summary>
/// Extension methods for configuring xUnit logging
/// </summary>
public static class XunitLoggingExtensions
{
    /// <summary>
    /// Adds xUnit test output logging to the logging builder
    /// </summary>
    public static ILoggingBuilder AddXunitLogging(this ILoggingBuilder builder, ITestOutputHelper output)
    {
        builder.ClearProviders();
        builder.AddProvider(new XunitLoggerProvider(output));
        builder.SetMinimumLevel(LogLevel.Debug);
        return builder;
    }
}