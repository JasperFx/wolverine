using Amazon.SQS.Model;
using Baseline;
using Oakton.Resources;
using Spectre.Console;
using Spectre.Console.Rendering;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsTransportStatefulResource : IStatefulResource
{
    private readonly AmazonSqsTransport _transport;
    private readonly IWolverineRuntime _runtime;

    public AmazonSqsTransportStatefulResource(AmazonSqsTransport transport, IWolverineRuntime runtime)
    {
        _transport = transport;
        _runtime = runtime;
    }

    public async Task Check(CancellationToken token)
    {
        var missing = new List<string>();
        using var client = _transport.BuildClient(_runtime);
        foreach (var queue in _transport.Queues)
        {
            try
            {
                await client.GetQueueUrlAsync(queue.QueueName, token);
            }
            catch (Exception)
            {
                missing.Add(queue.QueueName);
            }
        }

        if (missing.Any())
        {
            throw new Exception($"Missing known queues: {missing.Join(", ")}");
        }
    }

    public async Task ClearState(CancellationToken token)
    {
        using var client = _transport.BuildClient(_runtime);
        foreach (var queue in _transport.Queues)
        {
            await queue.PurgeAsync(client);
        }
    }

    public async Task Teardown(CancellationToken token)
    {
        using var client = _transport.BuildClient(_runtime);
        foreach (var queue in _transport.Queues)
        {
            await queue.TeardownAsync(client, token);
        }
    }

    public async Task Setup(CancellationToken token)
    {
        using var client = _transport.BuildClient(_runtime);
        foreach (var queue in _transport.Queues)
        {
            await queue.SetupAsync(client);
        }

    }

    public async Task<IRenderable> DetermineStatus(CancellationToken token)
    {
        var table = new Table
        {
            Alignment = Justify.Left
        };
        
        table.AddColumn("Queue");
        table.AddColumn("QueueUrl");
        
        using var client = _transport.BuildClient(_runtime);
        foreach (var queue in _transport.Queues)
        {
            try
            {
                var response = await client.GetQueueUrlAsync(queue.QueueName, token);
                table.AddRow(queue.QueueName, response.QueueUrl);
            }
            catch (Exception)
            {
                table.AddRow(new Markup(queue.QueueName), new Markup("[red]Does not exist[/]"));
            }
        }

        return table;
    }

    public string Type => "Amazon SQS";
    public string Name => TransportConstants.WolverineTransport;
}