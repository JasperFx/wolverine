using ImTools;
using LoadTesting.Trips;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace LoadTesting;

public class KickOffPublishing : IHostedService
{
    private readonly Publisher _publisher;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<KickOffPublishing> _logger;

    public KickOffPublishing(Publisher publisher, IWolverineRuntime runtime, ILogger<KickOffPublishing> logger)
    {
        _publisher = publisher;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bus = new MessageBus(_runtime);
        var messages = _publisher.InitialMessages();
        foreach (var message in messages)
        {
            
            await bus.PublishAsync(message);
            _logger.LogInformation("Published initial message {Message}", message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class Publisher
{
    private ImHashMap<Guid, TripStream> _streams = ImHashMap<Guid, TripStream>.Empty;

    public Publisher()
    {
        var streams = TripStream.RandomStreams(10);
        foreach (var stream in streams)
        {
            _streams = _streams.AddOrUpdate(stream.Id, stream);
        }
    }

    public IEnumerable<object> InitialMessages()
    {
        foreach (var entry in _streams.Enumerate())
        {
            if (entry.Value.TryCheckoutCommand(out var command))
            {
                yield return command;
            }
        }
    }

    public IEnumerable<object> NextMessages(Guid id)
    {
        if (_streams.TryFind(id, out var stream))
        {
            if (stream.TryCheckoutCommand(out var message))
            {
                yield return message;
            }

            if (stream.IsFinishedPublishing())
            {
                _streams = _streams = _streams.Remove(id);
            }
        }

        while (_streams.Count() < 10)
        {
            stream = new TripStream();
            _streams = _streams.AddOrUpdate(stream.Id, stream);

            if (stream.TryCheckoutCommand(out var message))
            {
                yield return message;
            }
        }
    }
}

public static class ContinueTripHandler
{
    public static IEnumerable<object> Handle(ContinueTrip message, Publisher publisher)
    {
        Thread.Sleep(250);
        
        return publisher.NextMessages(message.TripId);
    }
}