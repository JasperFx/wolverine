using System.Diagnostics.Metrics;
using Marten;
using Microsoft.Extensions.Hosting;

namespace EventPublisher;

internal class HostedPublisher : BackgroundService
{
    private readonly IDocumentStore _store;

    public HostedPublisher(IDocumentStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        
        var databases = await _store.Storage.AllDatabases();
        foreach (var database in databases)
        {
            for (var i = 0; i < 10; i++)
            {
                var publisher = new Publisher(_store, database, stoppingToken);
                tasks.Add(publisher.Start());
            }
        }

        await Task.WhenAll(tasks.ToArray());
    }
}