using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class MessagePump : IAsyncDisposable
{
    private IHost _host;

    public async Task StartHostAsync(WolverineMetricsMode mode)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Metrics.Mode = mode;
                opts.OnAnyException().RetryTimes(3).Then.MoveToErrorQueue();

                opts.LocalQueueFor<MessageHandlingMetrics>().Sequential();
            }).StartAsync();
    }

    public async Task PumpMessagesAsync(WolverineMetricsMode mode, TimeSpan duration)
    {
        if (_host == null)
        {
            await StartHostAsync(mode);
        }
        
        MetricsCollectionHandler.Clear();
        
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        while (stopwatch.ElapsedMilliseconds < duration.TotalMilliseconds)
        {
            var bus = _host.MessageBus();
            for (int i = 0; i < 100; i++)
            {
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M1(Guid.CreateVersion7()));
                }

                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M2(Guid.CreateVersion7()));
                }

                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M3(Guid.CreateVersion7(), Random.Shared.Next(0, 10)));
                }

                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M4(Guid.CreateVersion7(), Random.Shared.Next(0, 10)));
                }

                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M5(Guid.CreateVersion7()));
                }

            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable hostAsyncDisposable)
        {
            await hostAsyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}

public static class MetricsCollectionHandler
{
    public static ImmutableArray<MessageHandlingMetrics> Collected { get; private set; } 
        = ImmutableArray<MessageHandlingMetrics>.Empty;


    public static void Clear()
    {
        Collected = ImmutableArray<MessageHandlingMetrics>.Empty;
    }
    
    public static void Handle(MessageHandlingMetrics metrics)
    {
        Collected = Collected.Add(metrics);
    }
}

public record M1(Guid Id);
public record M2(Guid Id);

// Need to retry on errors to see that happen
public record M3(Guid Id, int Fails);
public record M4(Guid Id, int Fails);
public record M5(Guid Id);

public static class MessagesHandler
{
    public static async Task HandleAsync(M1 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M2 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M3 m, Envelope envelope)
    {
        if (m.Fails > envelope.Attempts)
        {
            throw new BadImageFormatException();
        }
        
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M4 m, Envelope envelope)
    {
        if (m.Fails > envelope.Attempts)
        {
            throw new DivideByZeroException();
        }
        
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M4 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
}