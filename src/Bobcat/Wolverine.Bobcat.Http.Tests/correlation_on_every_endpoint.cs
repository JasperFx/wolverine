using Alba;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Http;
using Wolverine.Http.Runtime;
using Wolverine.Marten;

namespace Wolverine.Bobcat.Http.Tests;

// GH-4939: WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints. By default RequestIdMiddleware applies
// X-Correlation-ID only to an endpoint that uses IMessageBus or IMessageContext, so an endpoint that only uses an
// event store session records some other correlation id on its events. Opted in, every endpoint takes the
// request's -- which is what lets ThenEventuallyCaused find what a posted command appended.

public abstract class CorrelationHost(string schema) : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
                m.DisableNpgsqlLogging = true;
                m.Events.MetadataConfig.CorrelationIdEnabled = true;
            })
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(CorrelationHost).Assembly);
        });

        builder.Services.AddWolverineHttp();

        Host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(ConfigureEndpoints)).StartAsync();
    }

    protected virtual void ConfigureEndpoints(WolverineHttpOptions opts)
    {
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();

    public async Task<IReadOnlyList<IEvent>> EventsCorrelatedWith(string correlationId)
    {
        await using var query = Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await query.Events.QueryAllRawEvents().Where(x => x.CorrelationId == correlationId).ToListAsync();
    }

    public async Task<IEvent> TheNoteFor(string patient)
    {
        await using var query = Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var notes = await query.Events.QueryAllRawEvents().Where(x => x.EventTypeName == "patient_note_added").ToListAsync();
        return notes.Single(x => ((PatientNoteAdded)x.Data).Patient == patient);
    }
}

public sealed class CorrelationOnEveryEndpointHost() : CorrelationHost("bobcat_http_correlation_on")
{
    protected override void ConfigureEndpoints(WolverineHttpOptions opts)
    {
        #region sample_apply_correlation_id_header_to_all_endpoints

        // app.MapWolverineEndpoints(opts => ...)
        // The request's X-Correlation-ID becomes the correlation id of every endpoint, applied before any
        // Marten, Polecat or Fisher session is opened, so the events an endpoint appends record it too
        opts.ApplyCorrelationIdHeaderToAllEndpoints = true;

        #endregion
    }
}

public sealed class CorrelationOnlyWhereMessagingIsUsedHost() : CorrelationHost("bobcat_http_correlation_off");

public class correlation_on_every_endpoint(CorrelationOnEveryEndpointHost app)
    : WolverineHttpSpec(app.Host), IClassFixture<CorrelationOnEveryEndpointHost>
{
    [Fact]
    public async Task an_endpoint_using_only_a_store_session_records_the_requests_correlation_id()
    {
        var correlationId = $"the-front-desk-{Guid.NewGuid():N}";
        var patient = $"Ann {Guid.NewGuid():N}";

        await app.Host.Scenario(x =>
        {
            x.Post.Json(new AddPatientNote(patient, "allergic to penicillin")).ToUrl("/api/patients/notes");
            x.WithRequestHeader(RequestIdMiddleware.CorrelationIdHeaderKey, correlationId);
            x.StatusCodeShouldBe(204);
        });

        var events = await app.EventsCorrelatedWith(correlationId);
        events.Select(x => x.Data).ShouldBe([new PatientNoteAdded(patient, "allergic to penicillin")]);
    }

    [Fact]
    public async Task then_eventually_caused_finds_what_a_posted_command_appended()
    {
        // WhenPosted sends the act's correlation id as X-Correlation-ID
        await WhenPosted(new AddPatientNote("Bea", "prefers mornings"));

        LastAct.CorrelationId.ShouldNotBeNull();
        await ThenEventuallyCaused(new PatientNoteAdded("Bea", "prefers mornings"));
    }
}

public class correlation_only_where_messaging_is_used(CorrelationOnlyWhereMessagingIsUsedHost app)
    : WolverineHttpSpec(app.Host), IClassFixture<CorrelationOnlyWhereMessagingIsUsedHost>
{
    [Fact]
    public async Task by_default_an_endpoint_using_only_a_store_session_does_not_take_the_header()
    {
        var correlationId = $"the-front-desk-{Guid.NewGuid():N}";
        var patient = $"Cal {Guid.NewGuid():N}";

        await app.Host.Scenario(x =>
        {
            x.Post.Json(new AddPatientNote(patient, "needs an interpreter")).ToUrl("/api/patients/notes");
            x.WithRequestHeader(RequestIdMiddleware.CorrelationIdHeaderKey, correlationId);
            x.StatusCodeShouldBe(204);
        });

        (await app.EventsCorrelatedWith(correlationId)).ShouldBeEmpty();

        // Today's behaviour: a correlation id of its own, an Activity id or a new Guid
        var note = await app.TheNoteFor(patient);
        note.CorrelationId.ShouldNotBeNull();
        note.CorrelationId.ShouldNotBe(correlationId);
    }

    [Fact]
    public async Task so_then_eventually_caused_cannot_find_what_a_posted_command_appended()
    {
        Scenario.EventuallyTimeout = TimeSpan.FromMilliseconds(300);

        await WhenPosted(new AddPatientNote("Dee", "no mornings"));

        await Should.ThrowAsync<Exception>(() => ThenEventuallyCaused(new PatientNoteAdded("Dee", "no mornings")));
    }
}
