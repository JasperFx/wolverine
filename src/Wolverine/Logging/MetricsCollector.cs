using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;

namespace Wolverine.Logging;

public class MetricsCollector : BackgroundService
{
    private readonly IMessageLogger _logger;
    private readonly IMetrics _metrics;
    private readonly IEnvelopePersistence _persistence;

    private readonly AdvancedSettings _settings;
    //private readonly IWorkerQueue _workers;

    public MetricsCollector(IMetrics metrics, IEnvelopePersistence persistence, IMessageLogger logger,
        AdvancedSettings settings)
    {
        _metrics = metrics;
        _persistence = persistence;
        _logger = logger;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_settings.MetricsCollectionSamplingInterval, stoppingToken);

            //_metrics.LogLocalWorkerQueueDepth(_workers.QueuedCount);

            try
            {
                var counts = await _persistence.Admin.FetchCountsAsync();
                _metrics.LogPersistedCounts(counts);
            }
            catch (Exception? e)
            {
                _logger.LogException(e);
            }
        }
    }
}
