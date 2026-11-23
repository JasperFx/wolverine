using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Http.Tests;

public record SaveThenPublishRequest5886(Guid BeforeId, Guid AfterId);

public record SaveThenPublishPublished5886(Guid Id);

public class SaveThenPublishDoc5886
{
    public Guid Id { get; set; }
}

public static class SaveThenPublishEndpoint5886
{
    // GH-5886: the endpoint commits its own session and then publishes. The chain's commit stores the later
    // message, and the end of the endpoint's pipeline has to send it
    [WolverinePost("/save-then-publish/5886")]
    public static async Task Post(SaveThenPublishRequest5886 request, IDocumentSession session, IMessageBus bus)
    {
        await bus.PublishAsync(new SaveThenPublishPublished5886(request.BeforeId));

        session.Store(new SaveThenPublishDoc5886 { Id = request.BeforeId });
        await session.SaveChangesAsync();

        await bus.PublishAsync(new SaveThenPublishPublished5886(request.AfterId));
    }
}

[WolverineIgnore]
public static class SaveThenPublishPublished5886Handlers
{
    public static readonly ConcurrentDictionary<Guid, int> Deliveries = new();

    public static void Handle(SaveThenPublishPublished5886 message)
        => Deliveries.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
}

public class save_then_publish_endpoint_5886
{
    [Fact]
    public async Task a_message_published_after_the_endpoints_own_save_is_sent()
    {
        var schema = "stp_" + Guid.NewGuid().ToString("N")[..8];
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            // Nothing but the flush at the end of the endpoint can deliver the message
            opts.Durability.DurabilityAgentEnabled = false;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(save_then_publish_endpoint_5886).Assembly);
            opts.Discovery.IncludeType(typeof(SaveThenPublishPublished5886Handlers));
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
            }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = schema);

            opts.Services.AddResourceSetupOnStartup();
        });

        builder.Services.AddWolverineHttp();
        await using var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints());

        var request = new SaveThenPublishRequest5886(Guid.NewGuid(), Guid.NewGuid());

        var tracked = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(_ => host.Scenario(x =>
            {
                x.Post.Json(request).ToUrl("/save-then-publish/5886");
                x.StatusCodeShouldBe(204);
            }));

        tracked.AllExceptions().ShouldBeEmpty();
        SaveThenPublishPublished5886Handlers.Deliveries.GetValueOrDefault(request.BeforeId).ShouldBe(1);
        SaveThenPublishPublished5886Handlers.Deliveries.GetValueOrDefault(request.AfterId).ShouldBe(1);
    }
}
