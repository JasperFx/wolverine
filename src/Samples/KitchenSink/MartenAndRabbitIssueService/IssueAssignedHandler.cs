using Baseline.Dates;
using Wolverine;
using MartenAndRabbitMessages;

namespace MartenAndRabbitIssueService;

public class IssueAssignedHandler
{
    private readonly Random _random = new Random();

    public async Task Handle(IssueAssigned assigned)
    {
        await Task.Delay(_random.Next(100, 2000));

    }

    public void Handle(IssueTimeout timeout)
    {
        Console.WriteLine("Checking timeout for " + timeout.IssueId);
    }

    public IssueAssigned Handle(AssignIssue command)
    {
        return new IssueAssigned(command.IssueId);
    }
}

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ICommandBus _bus;

    public Worker(ILogger<Worker> logger, ICommandBus bus)
    {
        _logger = logger;
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
            var issueId = Guid.NewGuid();
            await _bus.InvokeAsync(new IssueAssigned(issueId), stoppingToken);

            await _bus.ScheduleAsync(new IssueTimeout(issueId), 1.Minutes());
        }
    }
}
