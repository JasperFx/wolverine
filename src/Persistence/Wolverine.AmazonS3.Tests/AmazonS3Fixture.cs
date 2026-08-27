using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// One LocalStack bucket and one Wolverine host with S3 document persistence registered, shared by
/// the suites that need a real round trip.
/// </summary>
public class AmazonS3Fixture : IAsyncLifetime
{
    public AmazonS3Client Client { get; private set; } = null!;
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!LocalStack.IsRunning)
        {
            return;
        }

        Client = LocalStack.CreateClient();

        await EnsureBucketAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(InvoiceHandler));

                opts.Services.AddSingleton<IAmazonS3>(LocalStack.CreateClient());

                opts.UseAmazonS3Persistence(s3 =>
                {
                    s3.Store<InvoiceContent>(x =>
                    {
                        x.BucketName = InvoiceKeys.Bucket;
                        x.KeyFor = InvoiceKeys.For;
                    });
                });
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }

        Client?.Dispose();
    }

    public async Task EnsureBucketAsync()
    {
        try
        {
            await Client.PutBucketAsync(new PutBucketRequest { BucketName = InvoiceKeys.Bucket });
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    /// <summary>
    /// Write an invoice straight through the AWS client, so a test can prove Wolverine <em>read</em>
    /// rather than proving Wolverine can read back its own writes.
    /// </summary>
    public async Task PutAsync(InvoiceContent invoice, string? tenantId = null)
    {
        await Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = InvoiceKeys.Bucket,
            Key = InvoiceKeys.For(new S3KeyContext(typeof(InvoiceContent), invoice.Id, tenantId)),
            ContentBody = JsonSerializer.Serialize(invoice, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
    }

    public async Task<InvoiceContent?> GetAsync(string id, string? tenantId = null)
    {
        try
        {
            using var response = await Client.GetObjectAsync(InvoiceKeys.Bucket,
                InvoiceKeys.For(new S3KeyContext(typeof(InvoiceContent), id, tenantId)));

            using var reader = new StreamReader(response.ResponseStream);

            return JsonSerializer.Deserialize<InvoiceContent>(await reader.ReadToEndAsync(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string id, string? tenantId = null)
    {
        return Client.DeleteObjectAsync(InvoiceKeys.Bucket,
            InvoiceKeys.For(new S3KeyContext(typeof(InvoiceContent), id, tenantId)));
    }
}
