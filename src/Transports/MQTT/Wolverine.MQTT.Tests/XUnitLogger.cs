using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

public class XUnitLogger : ILogger
{
    private readonly string _categoryName;

    private readonly List<string> _ignoredStrings =
    [
        "Declared",
        "Successfully processed message"
    ];

    private readonly ITestOutputHelper _testOutputHelper;

    public XUnitLogger(ITestOutputHelper testOutputHelper, string categoryName)
    {
        _testOutputHelper = testOutputHelper;
        _categoryName = categoryName;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return new Disposable();
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (exception is DivideByZeroException)
        {
            return;
        }

        if (exception is BadImageFormatException)
        {
            return;
        }

        // if (_categoryName == "Wolverine.Runtime.WolverineRuntime" &&
        //     logLevel == LogLevel.Information) return;


        var text = formatter(state, exception);
        //if (_ignoredStrings.Any(x => text.Contains(x))) return;

        _testOutputHelper.WriteLine($"{_categoryName}/{logLevel}: {text}");

        if (exception != null)
        {
            _testOutputHelper.WriteLine(exception.ToString());
        }
    }

    public class Disposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}