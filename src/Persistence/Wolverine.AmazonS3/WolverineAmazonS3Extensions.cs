using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AmazonS3.Internals;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AmazonS3;

public static class WolverineAmazonS3Extensions
{
    /// <summary>
    /// Store the named document types as objects in Amazon S3, so a plain <c>[Entity]</c> parameter and
    /// the declarative <c>Storage.Store()</c> / <c>Storage.Delete()</c> return values resolve against a
    /// bucket.
    /// </summary>
    /// <remarks>
    /// Requires an <see cref="Amazon.S3.IAmazonS3" /> in the service container, left to the application
    /// so it keeps its own credential chain, region and retry policy. This does not make S3 the message
    /// store: the transactional inbox and outbox stay with whichever database the application uses.
    /// </remarks>
    /// <example>
    /// <code>
    /// opts.UseAmazonS3Persistence(s3 =&gt;
    /// {
    ///     s3.Store&lt;InvoiceContent&gt;(x =&gt;
    ///     {
    ///         x.BucketName = "invoice-content";
    ///         x.KeyFor = ctx =&gt; $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
    ///     });
    /// });
    /// </code>
    /// </example>
    public static WolverineOptions UseAmazonS3Persistence(this WolverineOptions options,
        Action<AmazonS3Configuration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new AmazonS3Configuration();
        configure(configuration);

        // Read back by S3PersistenceFrameProvider when the frames are generated.
        options.Services.AddSingleton(configuration);

        options.Services.AddScoped<IS3DocumentSession, S3DocumentSession>();
        options.Services.AddSingleton<IHostedService, AmazonS3StartupValidator>();

        options.CodeGeneration.InsertFirstPersistenceStrategy<S3PersistenceFrameProvider>();

        // Without this the generated code does not compile: it references S3StorageActionApplier.
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineAmazonS3Extensions).Assembly);

        return options;
    }
}
