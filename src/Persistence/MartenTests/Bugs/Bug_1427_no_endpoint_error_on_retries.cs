using IntegrationTests;
using JasperFx.Core;
using Marten;
using Marten.Exceptions;
using Marten.Schema;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_1427_no_endpoint_error_on_retries : IAsyncLifetime
{
    private IHost _host;

    public Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddMarten(o =>
        {
            // enabling writing private properties to the models
            o.UseNewtonsoftForSerialization(nonPublicMembersStorage: NonPublicMembersStorage.NonPublicSetters);

            o.Connection(Servers.PostgresConnectionString);
            o.AutoCreateSchemaObjects = AutoCreate.All;
            o.DatabaseSchemaName = "gh1427";
    
            o.DisableNpgsqlLogging = true;
    
            // register custom schema for module
            o.Schema.For<DomainObjectX>().DatabaseSchemaName("samplemodule");
    
        }).UseLightweightSessions().IntegrateWithWolverine();

        builder.Services.AddWolverine(options =>
        {
            options.Policies.UseDurableLocalQueues();
            options.Policies.AutoApplyTransactions();

            options.Policies.LogMessageStarting(LogLevel.Information);

            options.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

            if (builder.Environment.IsDevelopment())
            {
                options.Durability.Mode = DurabilityMode.Solo;
            }
    
            // ISSUE: this attempt to retry the failed messages leads to the "Wolverine.Runtime.Handlers.NoHandlerForEndpointException"
            options
                .OnException<ConcurrencyException>()
                .RetryWithCooldown(2.Seconds(), 4.Seconds(), 8.Seconds());
    
        });

        _host = builder.Build();
        return _host.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task send_a_bunch_of_batches_and_see_no_problems()
    {
        await _host.ExecuteAndWaitAsync(async c =>
        {
            await c.InvokeAsync(new StartBatch());
        }, 10000);
    }
}

[UseOptimisticConcurrency]
public class DomainObjectX
{
    public Guid Id { get; init; }
}

// this event is being consumed by multiple (in this case 2) handlers
public record MyEvent(string Id);

public record StartBatch;

public static class StartBatchHandler
{
    public static OutgoingMessages Handle(StartBatch message, IDocumentSession session)
    {
        // simply adding one document that will be used to trigger the exception
        session.Insert(new DomainObjectX { Id = Guid.NewGuid() });

        var messages = new OutgoingMessages();

        // 2 messages should be enough to trigger the issue.
        // If not, invoke the post again or increase the number.
        for (var i = 0; i < 2; i++)
        {
            messages.Add(new MyEvent(Guid.NewGuid().ToString()));
        }

        return messages;
    }
}