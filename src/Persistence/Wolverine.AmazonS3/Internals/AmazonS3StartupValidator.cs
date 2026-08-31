using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wolverine.AmazonS3.Internals;

/// <summary>
/// Refuses to start an application whose S3 document registration cannot work, where the client is
/// resolvable and the mappings are known, rather than letting a missing IAmazonS3 surface as a
/// container failure inside the first handler that happens to touch a document.
/// </summary>
internal class AmazonS3StartupValidator : IHostedService
{
    private readonly AmazonS3Configuration _configuration;
    private readonly ILogger<AmazonS3StartupValidator> _logger;
    private readonly IServiceProvider _services;

    public AmazonS3StartupValidator(AmazonS3Configuration configuration, IServiceProvider services,
        ILogger<AmazonS3StartupValidator> logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuration.Mappings.Count == 0)
        {
            _logger.LogWarning(
                "UseAmazonS3Persistence() was called but no document types were registered with Store<T>(), so nothing will resolve to S3");

            return Task.CompletedTask;
        }

        if (_services.GetService<IAmazonS3>() == null)
        {
            throw new InvalidOperationException(
                $"No IAmazonS3 is registered, but {_configuration.Mappings.Count} document type(s) are registered to be stored in S3. Register a client before UseAmazonS3Persistence() -- services.AddSingleton<IAmazonS3>(...) or AddAWSService<IAmazonS3>().");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
