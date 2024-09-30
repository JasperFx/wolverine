using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MartenTests.Distribution.Support;

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