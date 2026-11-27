using System.Collections.Concurrent;
using System.Diagnostics;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;

namespace Wolverine.Http.Tests;

/// <summary>
/// An audited member of an <c>[AsParameters]</c> type's <c>[FromBody]</c> member. The body overwrites the chain's
/// request type, but the body is only reachable through the <c>[AsParameters]</c> object, so the audit reads it from
/// there. GH-3135 covered audited members declared on the <c>[AsParameters]</c> type itself.
/// </summary>
public class audit_member_of_as_parameters_body : IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                // [Audit] isn't read off a Nullable<T> body, so a policy audits the struct body's member
                opts.ConfigureEndpoints(chain =>
                {
                    if (chain.RequestType == typeof(StructInvitePayload?))
                    {
                        chain.Audit(typeof(StructInvitePayload).GetProperty(nameof(StructInvitePayload.PersonId))!, "person.id");
                    }
                });

                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the audited [AsParameters] body endpoints",
                        type => type != typeof(AuditedAsParametersBodyEndpoints)));
            });
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _listener.Dispose();
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task an_audited_body_member_tags_the_request_span()
    {
        var personId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new InvitePayload(personId)).ToUrl("/api/audit-as-parameters/invitations");
            x.StatusCodeShouldBe(204);
        });

        RequestSpanFor("/api/audit-as-parameters/invitations").GetTagItem("person.id").ShouldBe(personId);
    }

    [Fact]
    public async Task audited_members_on_the_as_parameters_type_and_its_body_both_tag_the_request_span()
    {
        var teamId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new InvitePayload(personId)).ToUrl($"/api/audit-as-parameters/teams/{teamId}/invitations");
            x.StatusCodeShouldBe(204);
        });

        var request = RequestSpanFor("/api/audit-as-parameters/teams/{teamId}/invitations");
        request.GetTagItem("team.id").ShouldBe(teamId);
        request.GetTagItem("person.id").ShouldBe(personId);
    }

    [Fact]
    public async Task an_absent_optional_body_leaves_its_audited_member_untagged()
    {
        await theHost.Scenario(x =>
        {
            // An Alba scenario always says a request can have a body, so a bodiless POST says it has none
            x.Post.Url("/api/audit-as-parameters/optional-invitations");
            x.ConfigureHttpContext(c => c.Request.ContentLength = 0);
            x.StatusCodeShouldBe(204);
        });

        RequestSpanFor("/api/audit-as-parameters/optional-invitations").GetTagItem("person.id").ShouldBeNull();
    }

    [Fact]
    public async Task the_audit_reads_the_from_body_member_not_another_member_of_the_same_type()
    {
        var personId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new InvitePayload(personId)).ToUrl("/api/audit-as-parameters/reissued-invitations");
            x.StatusCodeShouldBe(204);
        });

        RequestSpanFor("/api/audit-as-parameters/reissued-invitations").GetTagItem("person.id").ShouldBe(personId);
    }

    [Fact]
    public async Task an_audited_member_of_a_nullable_struct_body_tags_the_request_span()
    {
        var personId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new StructInvitePayload(personId)).ToUrl("/api/audit-as-parameters/struct-invitations");
            x.StatusCodeShouldBe(204);
        });

        RequestSpanFor("/api/audit-as-parameters/struct-invitations").GetTagItem("person.id").ShouldBe(personId);
    }

    [Fact]
    public async Task an_audited_member_of_a_body_bound_through_a_renamed_constructor_parameter_tags_the_request_span()
    {
        var personId = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new InvitePayload(personId)).ToUrl("/api/audit-as-parameters/renamed-invitations");
            x.StatusCodeShouldBe(204);
        });

        RequestSpanFor("/api/audit-as-parameters/renamed-invitations").GetTagItem("person.id").ShouldBe(personId);
    }

    private Activity RequestSpanFor(string route)
        => _stopped.Where(x => x.GetTagItem("http.route") as string == route).ShouldHaveSingleItem();
}

public record InvitePayload([property: Audit("person.id")] Guid PersonId);

public record InviteCommand([FromBody] InvitePayload Body);

public record TeamInviteCommand([FromRoute] [property: Audit("team.id")] Guid TeamId, [FromBody] InvitePayload Body);

public record OptionalInviteCommand([FromBody] InvitePayload? Body);

public class ReissueInviteCommand
{
    // Declared ahead of the body and of the same type, so only the [FromBody] marker tells them apart
    public InvitePayload? Previous => null;

    [FromBody] public InvitePayload Body { get; set; } = null!;
}

public record struct StructInvitePayload(Guid PersonId);

public record StructInviteCommand([FromBody] StructInvitePayload? Body);

// The body binds through a constructor parameter whose name no property shares
public class RenamedInviteCommand([FromBody] InvitePayload payload)
{
    public InvitePayload Body { get; } = payload;
}

public static class AuditedAsParametersBodyEndpoints
{
    [WolverinePost("/api/audit-as-parameters/renamed-invitations"), EmptyResponse]
    public static Task InviteRenamed([AsParameters] RenamedInviteCommand command) => Task.CompletedTask;

    [WolverinePost("/api/audit-as-parameters/invitations"), EmptyResponse]
    public static Task Invite([AsParameters] InviteCommand command) => Task.CompletedTask;

    [WolverinePost("/api/audit-as-parameters/teams/{teamId}/invitations"), EmptyResponse]
    public static Task InviteToTeam([AsParameters] TeamInviteCommand command) => Task.CompletedTask;

    [WolverinePost("/api/audit-as-parameters/optional-invitations"), EmptyResponse]
    public static Task InviteOptionally([AsParameters] OptionalInviteCommand command) => Task.CompletedTask;

    [WolverinePost("/api/audit-as-parameters/reissued-invitations"), EmptyResponse]
    public static Task Reissue([AsParameters] ReissueInviteCommand command) => Task.CompletedTask;

    [WolverinePost("/api/audit-as-parameters/struct-invitations"), EmptyResponse]
    public static Task InviteByStruct([AsParameters] StructInviteCommand command) => Task.CompletedTask;
}
