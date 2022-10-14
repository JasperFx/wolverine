using Amazon.Runtime;
using Amazon.SQS;
using Xunit.Abstractions;

namespace Wolverine.AmazonSqs.Tests;

public class Playing
{
    private readonly ITestOutputHelper _output;
    
    private static string SecretKey = "ignore";
    private static string AccessKey = "ignore";

    public Playing(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task try_to_find_queues()
    {
        var credentials = new BasicAWSCredentials(AccessKey, SecretKey);
        var config = new AmazonSQSConfig
        {
            ServiceURL = "http://localhost:4566"
        };
        
        
        
        using var client = new AmazonSQSClient(credentials, config);

        
        await client.CreateQueueAsync("wolverine-foo");
        
        
        var queues = await client.ListQueuesAsync("wolverine");
        foreach (var queueUrl in queues.QueueUrls)
        {
            _output.WriteLine(queueUrl);
        }
    }
}