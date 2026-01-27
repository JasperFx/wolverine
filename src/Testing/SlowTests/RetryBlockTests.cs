using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace SlowTests;

public class RetryBlockTests
{
    private readonly RetryBlock<SometimesFailingMessage> theBlock;
    private readonly SpyLogger theLogger = new();
    private readonly SometimesFailingMessageHandler theHandler;

    public RetryBlockTests()
    {
        theHandler = new SometimesFailingMessageHandler();
        theBlock = new RetryBlock<SometimesFailingMessage>(theHandler, theLogger,
            CancellationToken.None);

        theBlock.Pauses = [1.Seconds(), 3.Seconds(), 5.Seconds()];
    }

    [Fact]
    public void get_pause()
    {
        theBlock.DeterminePauseTime(1).ShouldBe(1.Seconds());
        theBlock.DeterminePauseTime(2).ShouldBe(3.Seconds());
        theBlock.DeterminePauseTime(3).ShouldBe(5.Seconds());
        theBlock.DeterminePauseTime(4).ShouldBe(5.Seconds());
        theBlock.DeterminePauseTime(5).ShouldBe(5.Seconds());
    }

    [Fact]
    public async Task run_successfully()
    {
        theBlock.Post(new SometimesFailingMessage(0, "Aubrey"));

        await theBlock.DrainAsync();

        theLogger.Messages[LogLevel.Debug].Single()
            .ShouldBe("Completed Name: Aubrey");
    }

    [Fact]
    public async Task retry_within_threshold()
    {
        var theMessage = new SometimesFailingMessage(2, "Aubrey");
        theBlock.Post(theMessage);
        await theMessage.Completion;

        theLogger.Exceptions.Count.ShouldBe(2);
        theLogger.Messages[LogLevel.Error].Count.ShouldBe(2);

        theLogger.Messages[LogLevel.Debug].Single()
            .ShouldBe("Completed Name: Aubrey");
    }

    [Fact]
    public async Task disregard_after_too_many_failures()
    {
        var theMessage = new SometimesFailingMessage(5, "Aubrey");
        theBlock.Post(theMessage);

        theBlock.Pauses = [0.Milliseconds(), 50.Milliseconds()];

        var tries = 0;
        while (tries < 10 && !theLogger.Messages[LogLevel.Information].Any())
        {
            tries++;
            await Task.Delay(100.Milliseconds());
        }

        theLogger.Messages[LogLevel.Information].Single()
            .ShouldBe("Discarding message Name: Aubrey after 3 attempts");
    }
}

public class SometimesFailingMessageHandler : IItemHandler<SometimesFailingMessage>
{
    public Task ExecuteAsync(SometimesFailingMessage message, CancellationToken cancellation)
    {
        message.Attempts++;
        if (message.Attempts <= message.Fails)
        {
            throw new InvalidOperationException("You cannot pass!");
        }

        message.Complete();

        return Task.CompletedTask;
    }
}

public class SometimesFailingMessage
{
    private readonly TaskCompletionSource<SometimesFailingMessage> _completion = new();

    public SometimesFailingMessage(int fails, string name)
    {
        Fails = fails;
        Name = name;
    }

    public int Fails { get; }
    public string Name { get; }
    public int Attempts { get; set; }

    public Task Completion => _completion.Task;

    public void Complete()
    {
        _completion.SetResult(this);
    }

    public override string ToString()
    {
        return $"{nameof(Name)}: {Name}";
    }
}

public class SpyLogger : ILogger, IDisposable
{
    public readonly List<Exception> Exceptions = [];
    public readonly LightweightCache<LogLevel, List<string>> Messages = new(_ => []);

    public void Dispose()
    {
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages[logLevel].Add(formatter(state, exception));
        if (exception != null)
        {
            Exceptions.Add(exception);
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return this;
    }
}