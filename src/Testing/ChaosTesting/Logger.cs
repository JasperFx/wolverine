using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace ChaosTesting;

public class OutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public OutputLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }


    public void Dispose()
    {
        
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_output, categoryName);
    }
}

public class XUnitLogger : ILogger
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly string _categoryName;

    public XUnitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable BeginScope<TState>(TState state) => new Disposable();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (exception is DivideByZeroException) return;
        if (exception is BadImageFormatException) return;

        if (_categoryName == "Wolverine.Runtime.WolverineRuntime/Information" &&
            logLevel == LogLevel.Information) return;
        
        var text = formatter(state, exception);
        
        _testOutputHelper.WriteLine($"{_categoryName}/{logLevel}: {text}");
        
        if (exception != null)
        {
            _testOutputHelper.WriteLine(exception.ToString());
        }
    }
}