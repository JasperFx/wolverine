using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

public class When_handling_messages_in_saga
{
    [Fact]
    public async Task should_not_throw()
    {
        using var host =
            await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "saga_messages";
                    }).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

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
                    opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "saga_messages";
                    }).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();
                })
                .StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

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
                    // No Polecat integration - in-memory only
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
                    // No Polecat integration - in-memory only
                })
                .StartAsync();

        var subscriptionId = Guid.NewGuid();

        var subscribed = new Subscribed("Acme, Inc", subscriptionId.ToString());
        await host.InvokeMessageAndWaitAsync(subscribed);

        UserRegistrationSaga.NotFoundSubscribed.ShouldBe(subscribed);
    }
}
