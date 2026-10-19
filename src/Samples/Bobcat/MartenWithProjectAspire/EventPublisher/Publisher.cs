using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Services;
using Marten.Storage;
using TripDomain;

namespace EventPublisher;

internal class Publisher
{
    private readonly IMartenDatabase _database;
    private readonly CancellationToken _stoppingToken;
    private readonly string _name;
    private readonly IDocumentStore _store;

    public Publisher(IDocumentStore store, IMartenDatabase database, CancellationToken stoppingToken)
    {
        _store = store;
        _database = database;
        _stoppingToken = stoppingToken;

        var storeName = store.GetType() == typeof(DocumentStore) ? "Marten" : store.GetType().NameInCode();
        _name = $"{storeName}:{_database.Identifier}";
    }

    public Task Start()
    {
        var random = Random.Shared;
        return Task.Run(async () =>
        {
            while (!_stoppingToken.IsCancellationRequested)
            {
                var delay = random.Next(0, 250);

                await Task.Delay(delay.Milliseconds(), _stoppingToken);
                await PublishEvents();
            }
        }, _stoppingToken);
    }

    public async Task PublishEvents()
    {
        var streams = TripStream.RandomStreams(5);
        while (streams.Any())
        {
            var options = SessionOptions.ForDatabase(_database);

            await using var session = _store.LightweightSession(options);
            foreach (var stream in streams.ToArray())
            {
                if (stream.TryCheckOutEvents(out var events))
                {
                    session.Events.Append(stream.StreamId, events);
                }

                if (stream.IsFinishedPublishing())
                {
                    streams.Remove(stream);
                }
            }

            await session.SaveChangesAsync(_stoppingToken);
        }
    }
}