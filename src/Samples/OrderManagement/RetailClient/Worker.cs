using JasperFx.Core;
using Marten;
using Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orders;
using Wolverine;

namespace RetailClient;

public class Worker : BackgroundService
{
    private readonly IMessageBus _bus;
    private readonly ILogger<Worker> _logger;
    private readonly IDocumentStore _store;

    public Worker(
        ILogger<Worker> logger,
        IMessageBus bus,
        IDocumentStore store
    )
    {
        _logger = logger;
        _bus = bus;
        _store = store;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken
    )
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Console.WriteLine("Create new order?");
                Console.ReadLine();

                await using var session = _store.QuerySession();

                var customers = await session.Query<Customer>().ToListAsync();
                var random = new Random();
                var randomIndex = random.Next(0, customers.Count() - 1);
                var randomCustomer = customers.ElementAt(randomIndex);

                var randomAmount = random.Next(1000, 10000);

                _logger.LogInformation("Placing a new Order");
                _logger.LogInformation("Customer: {RandomCustomerName}", randomCustomer.Name);
                _logger.LogInformation("Amount: {RandomAmount}", randomAmount);

                var order = await _bus.InvokeAsync<PurchaseOrder?>(
                    new PlaceOrder(
                        $"{Guid.NewGuid()}",
                        randomCustomer.Id,
                        randomAmount
                    ), timeout:30.Seconds()
                );

                Console.WriteLine($"OrderId: {order?.Id}");
            }
        }
    }
}