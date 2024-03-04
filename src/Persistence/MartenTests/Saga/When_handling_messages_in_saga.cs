using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class When_handling_messages_in_saga : PostgresqlContext
{
    [Fact]
    public async Task should_not_throw()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(Servers.PostgresConnectionString)
                        .IntegrateWithWolverine();
                    
                    opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        var subscriptionId = Guid.NewGuid();


        await host.InvokeMessageAndWaitAsync(
            new Registered(
                "ACME, Inc",
                "Jane",
                "Doe",
                "jd@acme.inc",
                subscriptionId.ToString()
            )
        );
    }

    [Fact]
    public async Task will_not_throw_nonexistent_document_in_not_found()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(Servers.PostgresConnectionString)
                        .IntegrateWithWolverine();
                    
                    opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        var subscriptionId = Guid.NewGuid();

        var subscribed = new Subscribed("Acme, Inc", subscriptionId.ToString());
        await host.InvokeMessageAndWaitAsync(subscribed);
        
        UserRegistrationSaga.NotFoundSubscribed.ShouldBe(subscribed);
    }
    
    [Fact]
    public async Task should_not_throw_in_memory()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // opts.Services.AddMarten(Servers.PostgresConnectionString)
                    //     .IntegrateWithWolverine();
                    //
                    // opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        var subscriptionId = Guid.NewGuid();


        await host.InvokeMessageAndWaitAsync(
            new Registered(
                "ACME, Inc",
                "Jane",
                "Doe",
                "jd@acme.inc",
                subscriptionId.ToString()
            )
        );
    }

    [Fact]
    public async Task will_not_throw_nonexistent_document_in_not_found_in_memory()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // opts.Services.AddMarten(Servers.PostgresConnectionString)
                    //     .IntegrateWithWolverine();
                    //
                    // opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        var subscriptionId = Guid.NewGuid();

        var subscribed = new Subscribed("Acme, Inc", subscriptionId.ToString());
        await host.InvokeMessageAndWaitAsync(subscribed);
        
        UserRegistrationSaga.NotFoundSubscribed.ShouldBe(subscribed);
    }
}